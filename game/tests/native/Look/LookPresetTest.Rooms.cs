using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// The Lane L fix round (review M1 to M8 and the minors) and the art pass: rooms other than the test room (a site of their
/// own, a window that is only an opening, no sun hint, three windows, single-sided captured walls), the sky a window shows,
/// focus that eases, the observe view, the focus pass of the post effect, the season looks, and the light protections that
/// five mutations once removed without a test noticing.
/// </summary>
public partial class LookPresetTest
{
    // ---------- the room's site (review M5) ----------

    private async Task CheckSite(StylePreset preset, RoomData room)
    {
        const string good = """{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 90, "solar_noon_h": 12.5 } }""";
        var site = RoomSite.FromManifest(Encoding.UTF8.GetBytes(good));
        Check(site is { LatitudeDeg: 45f, NegZBearingDeg: 90f, SolarNoonH: 12.5f, Declared: true } && site.Hemisphere == "north", "a room's site key is read: latitude, which way -Z faces, solar noon");
        Check(RoomSite.FromManifest(Encoding.UTF8.GetBytes("{}")) == null, "a manifest without a site gives none");
        foreach (var (text, fragment, label) in new[]
        {
            ("""{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 0, "solar_noon_h": 12, "longitude_deg": 8 } }""", "longitude_deg", "a longitude is refused: a site never says where a room is"),
            ("""{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 0, "solar_noon_h": 12, "place": "Oslo" } }""", "place", "a place name is refused"),
            ("""{ "site": { "latitude_deg": 45.5, "neg_z_bearing_deg": 0, "solar_noon_h": 12 } }""", "latitude_deg", "a latitude finer than a whole degree is refused"),
            ("""{ "site": { "latitude_deg": 70, "neg_z_bearing_deg": 0, "solar_noon_h": 12 } }""", "latitude_deg", "a latitude beyond 66 degrees is refused"),
            ("""{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 360, "solar_noon_h": 12 } }""", "neg_z_bearing_deg", "a bearing of 360 is refused"),
            ("""{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 0, "solar_noon_h": 12.1 } }""", "solar_noon_h", "a solar noon off the quarter hour is refused"),
            ("""{ "site": { "latitude_deg": 45, "neg_z_bearing_deg": 0 } }""", "solar_noon_h", "a site missing a key is refused, naming it"),
            ("""{ "site": [] }""", "site", "a site that is not an object is refused"),
        })
            CheckThrows(() => RoomSite.FromManifest(Encoding.UTF8.GetBytes(text)), fragment, label);

        // The test room declares the site the look used to assume, so its sun is unchanged and nothing is said about it.
        var declared = RoomSite.For(room, out var declaredNote);
        Check(declared.Declared && declared is { LatitudeDeg: 30f, NegZBearingDeg: 0f, SolarNoonH: 12f } && declaredNote.Length == 0,
            "the test room declares its site: 30 degrees north, -Z facing north, solar noon at 12");
        // A room without a site gets the fallback and says so; the shared preset never carries one.
        var siteless = RoomData.Load(LookFixtureRooms.Write("site_none", r => r.Remove("site")));
        var plain = RoomSite.For(siteless, out var warning);
        Check(!plain.Declared && plain == RoomSite.Fallback && warning.Contains("declares no site"), "a room without a site: the look says so and uses its fallback");
        var (holder, look) = NewDirector(preset, siteless, "SiteFallbackHolder");
        Check(look.SiteNote.Contains("declares no site") && !look.Warnings.Any(w => w.Contains("site")) && look.DescribeLook().Contains($"{preset.PresetId}@{preset.PresetVersion}"),
            "a missing site is information, not a warning: the review capture does not fail on it");
        Check(look.Preset != preset && look.Preset.Sha256 == preset.Sha256 && preset.Tuning.Sun.LatitudeDeg == 30f, "the look works with the preset plus the site; the shared preset object is not changed");
        holder.QueueFree();

        // A room with a site gets its own sun.
        var oslo = RoomData.Load(LookFixtureRooms.Write("site_north", r => r["site"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "latitude_deg": 60, "neg_z_bearing_deg": 90, "solar_noon_h": 12.5 }""")));
        var (northHolder, north) = NewDirector(preset, oslo, "SiteNorthHolder");
        Check(north.SiteNote.Length == 0 && north.Preset.Site.Declared && north.Preset.Tuning.Sun.LatitudeDeg == 60f && north.Preset.Tuning.Sun.NegZBearingDeg == 90f
            && north.Preset.Tuning.Sun.SolarNoonH == 12.5f, "a room's own site reaches the sun: latitude, facing and solar noon");
        var plainDay = LookClock.DayLength(preset, 172);
        var northDay = LookClock.DayLength(north.Preset, 172);
        Check(plainDay is > 13.8f and < 14.4f && northDay > 17.5f, $"the longer June day at 60 degrees north ({northDay:0.0} h, against {plainDay:0.0} h at the fallback 30)");
        // Facing: with -Z facing east (90), the evening sun in the west is at +Z... the room turns under a fixed sun.
        north.SetClock(17f, 172);
        var turned = north.Moment;
        var facing0 = LookClock.Yaw(preset.Tuning.Sun, turned.SunBearingDeg);
        var facing90 = LookClock.Yaw(north.Preset.Tuning.Sun, turned.SunBearingDeg);
        Check(Mathf.Abs(Mathf.Wrap(facing90 - facing0, -180f, 180f) - 90f) < 0.01f, "turning the room (-Z facing east instead of north) turns the sun's direction in it by the same quarter turn");
        Check(north.Preset.Tuning.Sun.RealClockDaylightSaving == preset.Tuning.Sun.RealClockDaylightSaving && north.Preset.Tuning.Sun.MoonElevationDeg == preset.Tuning.Sun.MoonElevationDeg,
            "everything of the sun's tuning but the site still comes from the preset");
        northHolder.QueueFree();
        // The southern hemisphere has its own seasons: 15 July is winter at 30 degrees south.
        var south = RoomData.Load(LookFixtureRooms.Write("site_south", r => r["site"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "latitude_deg": -30, "neg_z_bearing_deg": 180, "solar_noon_h": 12 }""")));
        var (southHolder, southLook) = NewDirector(preset, south, "SiteSouthHolder");
        southLook.SetClock(12f, 196);
        Check(southLook.Preset.Hemisphere == "south" && southLook.Moment.Season == "winter", $"a room south of the equator has the seasons turned round: mid-July is {southLook.Moment.Season}");
        southHolder.QueueFree();
        // The look reads the manifest only while it still has the hash the loader verified.
        var directory = LookFixtureRooms.Write("site_swapped", _ => { });
        var loaded = RoomData.Load(directory);
        var edited = FileAccess.GetFileAsBytes(directory + "/room.json");
        var swapped = Encoding.UTF8.GetString(edited).Replace("\"units\": \"m\"", "\"site\": { \"latitude_deg\": 10, \"neg_z_bearing_deg\": 0, \"solar_noon_h\": 12 },\n  \"units\": \"m\"");
        using (var file = FileAccess.Open(directory + "/room.json", FileAccess.ModeFlags.Write)) file.StoreBuffer(Encoding.UTF8.GetBytes(swapped));
        var afterSwap = RoomSite.For(loaded, out var swapWarning);
        Check(!afterSwap.Declared && swapWarning.Contains("changed since it was loaded"), "a manifest changed after loading is not read for its site: " + swapWarning);
        // A bad site is no site: the fallback and a note, never a crash.
        var bad = RoomData.Load(LookFixtureRooms.Write("site_bad", r => r["site"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "latitude_deg": 12.5, "neg_z_bearing_deg": 0, "solar_noon_h": 12 }""")));
        var unusable = RoomSite.For(bad, out var badWarning);
        Check(!unusable.Declared && badWarning.Contains("latitude_deg") && badWarning.Contains("fallback"), "a room whose site is unusable falls back, saying why: " + badWarning);
        await Frames(1);
    }

    // ---------- the sun and the windows (review M1, M3) ----------

    private async Task CheckSunAndWindows(StylePreset preset, RoomData room)
    {
        var (h0, test) = NewDirector(preset, room, "SunTestRoomHolder");
        Check(test.SunScale == 1f && test.Key.Visible, "a room with a sun hint gets the sun at that hint's strength");
        h0.QueueFree();
        // M1: a window without a sun hint still lets the sun in.
        var windowOnly = RoomData.Load(LookFixtureRooms.WindowWithoutSunHint());
        var (h1, noHint) = NewDirector(preset, windowOnly, "SunWindowOnlyHolder");
        Check(noHint.SunScale == 1f && noHint.Key.Visible && noHint.Key.LightEnergy > 0.1f, "a room with a window but no sun hint still gets the sun, through the window (review M1: captured rooms were black by day)");
        h1.QueueFree();
        var captured = RoomData.Load(LookFixtureRooms.CapturedLike());
        var (h2, capturedLook) = NewDirector(preset, captured, "SunCapturedHolder");
        capturedLook.SetClock(12f, 196);
        Check(capturedLook.SunScale == 1f && capturedLook.Key.Visible && capturedLook.SkyFills.Count == 1, "a captured-like room (the window only listed as an opening, no sun hint) has the sun and a sky fill");
        h2.QueueFree();
        // A closed room with no window and no sun hint stays a box lit by its lamps.
        var closed = RoomData.Load(LookFixtureRooms.Write("closed_room", r =>
        {
            LookFixtureRooms.RemoveSunHint(r);
            var hints = LookFixtureRooms.Hints(r);
            foreach (var hint in hints.Where(x => x!["kind"]!.GetValue<string>() == "window").ToArray()) hints.Remove(hint);
        }));
        var (h3, closedLook) = NewDirector(preset, closed, "SunClosedHolder");
        Check(closedLook.SunScale == 0f && !closedLook.Key.Visible && closedLook.SkyFills.Count == 0 && closedLook.Lamps.Count == 1, "a room with no window and no sun hint gets no sun and no sky: only its lamp");
        h3.QueueFree();

        // M3: a third window must not shine through its wall.
        var three = RoomData.Load(LookFixtureRooms.ThreeWindows());
        var (h4, threeLook) = NewDirector(preset, three, "ThreeWindowsHolder");
        var fills = threeLook.SkyFills.OrderByDescending(f => f.GetMeta("base_energy").AsDouble()).ToArray();
        var shadowedCount = fills.Count(f => f.ShadowEnabled);
        var allShadowed = threeLook.GetChildren().OfType<Light3D>().Count(l => l.ShadowEnabled);
        Check(fills.Length == 3 && shadowedCount == 2 && fills[0].ShadowEnabled && fills[1].ShadowEnabled && !fills[2].ShadowEnabled && allShadowed <= preset.MaxShadowedLights,
            $"three windows share the shadow budget: the two brightest get shadows ({shadowedCount} of 3), within {preset.MaxShadowedLights} in all ({allShadowed})");
        var bounds = three.Bounds;
        Check(fills.Where(f => f.ShadowEnabled).All(f => !bounds.HasPoint(f.Position)), "a shadowed sky fill stands outside the room, behind the opening, so the wall blocks it");
        Check(!fills[2].ShadowEnabled && bounds.HasPoint(fills[2].Position) && fills[2].Position.DistanceTo(three.LightHints.First(h => h.Id == fills[2].GetMeta("light_hint_id").AsString()).PositionM!.Value) < 0.5f,
            $"an unshadowed sky fill stands inside the room at its opening ({fills[2].Position}), never behind the wall where it would shine through (review M3)");
        h4.QueueFree();
        var inner = LookDirector.SkyFillPosition(new Aabb(new Vector3(-2, 0, -1.5f), new Vector3(4, 2.4f, 3)), new Vector3(-2.15f, 1.25f, 0.3f), Vector3.Right, 0.6f, false);
        var outer = LookDirector.SkyFillPosition(new Aabb(new Vector3(-2, 0, -1.5f), new Vector3(4, 2.4f, 3)), new Vector3(-2.15f, 1.25f, 0.3f), Vector3.Right, 0.6f, true);
        Check(inner.IsEqualApprox(new Vector3(-1.98f, 1.25f, 0.3f)) && outer.IsEqualApprox(new Vector3(-2.75f, 1.25f, 0.3f)), "the sky fill's stand: 2 cm inside the bounds without a shadow, a standoff behind the opening with one");
        await Frames(1);
    }

    // ---------- the sky a window shows (review M2) ----------

    private async Task CheckWindowSky(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "SkyHolder");
        var material = look.Environment.Sky?.SkyMaterial as ShaderMaterial;
        Check(look.Environment.BackgroundMode == Godot.Environment.BGMode.Sky && material != null && material.Shader.ResourcePath == LookDirector.SkyShaderPath,
            "a window shows a sky, not the preset's flat slate (review M2): the environment's background is the painterly window sky");
        Check(look.Environment.AmbientLightSource == Godot.Environment.AmbientSource.Color && look.Environment.ReflectedLightSource == Godot.Environment.ReflectionSource.Disabled,
            "the sky lights nothing: ambient and reflections stay off, so the room's exposure is unchanged");
        var shader = GD.Load<Shader>(LookDirector.SkyShaderPath);
        var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        var probe = new ShaderMaterial { Shader = shader };
        LookSky.Apply(probe, LookSky.At(preset, LookClock.At(preset, 12f, 196)));
        var set = new[] { "zenith", "horizon", "sun_color", "to_sun", "sun_visible", "moon_color", "to_moon", "moon_visible", "cloud_light", "cloud_shade", "cloud_amount", "brightness", "stars", "sun_disc", "sun_halo", "moon_disc", "ground" };
        Check(declared.SetEquals(set), "every uniform of the sky shader is set by the look, and the look sets no other: " + string.Join(",", declared.Except(set).Concat(set.Except(declared))));

        look.SetClock(12f, 196);
        var noon = LookSky.At(look.Preset, look.Moment);
        var slate = ColorGrade.Luma(preset.Background);
        Check(ColorGrade.Luma(noon.Horizon) > 2.5f * slate && noon.Zenith.B > noon.Zenith.R + 0.2f && noon.Brightness >= 1.2f && noon.SunVisible > 0.99f && noon.MoonVisible < 0.01f,
            $"at noon in July the sky is light, blue and bright (horizon luma {ColorGrade.Luma(noon.Horizon):0.00} against the old slate's {slate:0.00}), with the sun up");
        Check(Mathf.IsEqualApprox((float)((Color)material!.GetShaderParameter("zenith")).R, noon.Zenith.R) && ((Vector3)material.GetShaderParameter("to_sun")).DistanceTo(noon.ToSun) < 0.001f,
            "the shader holds the sky of the moment");
        var key = look.Key.GlobalBasis.Z.Normalized();
        Check(key.DistanceTo(noon.ToSun) < 0.002f, "the sun's disc in the sky is where the sun light comes from");
        look.SetClock(LookClock.SunTimes(look.Preset.Tuning.Sun, 279).Sunset - 0.4f, 279);
        var golden = LookSky.At(look.Preset, look.Moment);
        Check(golden.Horizon.R - golden.Horizon.B > noon.Horizon.R - noon.Horizon.B + 0.1f && golden.CloudLight.R >= golden.CloudLight.B,
            "near sunset the horizon turns warm and the clouds catch the sun's colour");
        look.SetClock(2f, 279);
        var night = LookSky.At(look.Preset, look.Moment);
        Check(ColorGrade.Luma(night.Zenith) < 0.12f && night.Zenith.B > night.Zenith.R + 0.05f && night.MoonVisible > 0.99f && night.Stars > 0.3f && night.SunVisible < 0.01f,
            "at 02:00 the sky is deep blue with the moon and stars, and no sun");
        // Seasons: a summer sky is clearer and bluer, a winter one paler and cloudier.
        look.SetClock(12f, 196);
        var summer = LookSky.At(look.Preset, look.Moment);
        look.SetClock(12f, 15);
        var winter = LookSky.At(look.Preset, look.Moment);
        Check(summer.Zenith.S > winter.Zenith.S + 0.1f && summer.CloudAmount < winter.CloudAmount - 0.2f && summer.Brightness > winter.Brightness,
            $"a summer sky is clearer, more saturated and brighter than a winter one (saturation {summer.Zenith.S:0.00} against {winter.Zenith.S:0.00}, cloud {summer.CloudAmount:0.00} against {winter.CloudAmount:0.00})");
        holder.QueueFree();
        await Frames(1);
    }

    // ---------- single-sided captured walls and captured materials (review M4 and a minor) ----------

    private async Task CheckCapturedWalls(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var room = RoomData.Load(LookFixtureRooms.SingleSidedWestWall());
        var (holder, look) = NewDirector(preset, room, "CapturedWallHolder");
        var built = RoomBuilder.Build(room);
        holder.AddChild(built);
        await Frames(2);
        var wall = built.GetNode("Shell").GetChildren().OfType<Node3D>().First(n => n.GetMeta("entity_id").AsString() == "shell:wall_west");
        var meshes = wall.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
        Check(meshes.Length == 1 && meshes[0].GetParent().GetParent() == wall || meshes.Length == 1, "the single-sided wall arrives as one mesh");
        var quad = meshes[0];
        Check(quad.CastShadow == GeometryInstance3D.ShadowCastingSetting.DoubleSided,
            "a single-sided shell mesh casts shadows from both sides, so the sun outside cannot pass through it (review M4)");
        var painted = quad.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
        Check(painted != null && MaterialLibrary.IsPainterly(painted) && painted.GetMeta("material_role").AsString() == "painted_wall" && painted.GetMeta("captured").AsBool(),
            "a captured mesh's plain glTF material becomes the painterly material of its role (review minor: captured assets got no treatment)");
        Check(painted != null && painted.GetShaderParameter("photo_mix").AsSingle() == 1f && painted.GetShaderParameter("photo_texture").AsGodotObject() is Texture2D
            && quad.GetActiveMaterial(0) == painted && quad.HasMeta(LookDirector.CapturedPaintedMeta),
            "the painterly material keeps the capture's own colour texture, and the instance wears it");
        Check(quad.Mesh.SurfaceGetMaterial(0) is StandardMaterial3D, "the mesh's own glTF material stays on the mesh");
        Check(quad.GetInstanceShaderParameter("paint_seed").VariantType != Variant.Type.Nil, "a captured mesh gets its own paint seed too");
        Material? during = new StandardMaterial3D();
        LookDirector.WithBakeStandIns(meshes, () => during = quad.GetSurfaceOverrideMaterial(0));
        Check(during == null && quad.GetSurfaceOverrideMaterial(0) == painted, "during a GI bake the capture's own materials are voxelized, and the painterly ones come back after");
        var threw = false;
        try { LookDirector.WithBakeStandIns(meshes, () => throw new InvalidOperationException("bake failed")); } catch (InvalidOperationException) { threw = true; }
        Check(threw && quad.GetSurfaceOverrideMaterial(0) == painted, "a failed bake still restores the painterly material");
        Check(LookDirector.RoleFor(wall, "anything") == "painted_wall", "a shell part's role is its own material role");
        // Objects: the slot named like the glTF material, else the first role.
        var body = new Node3D();
        body.SetMeta("material_roles", new Godot.Collections.Dictionary { ["legs"] = "metal", ["top"] = "wood" });
        var plain = new Node3D();
        Check(LookDirector.RoleFor(body, "top") == "wood" && LookDirector.RoleFor(body, "unknown") == "metal" && LookDirector.RoleFor(plain, "x") == "default",
            "an object's surface takes the role of the slot named like its glTF material, else the asset's first role");
        body.Free();
        plain.Free();
        holder.QueueFree();
        await Frames(1);
    }

    // ---------- depth of field: easing and the observe view ----------

    private async Task CheckFocusEasing(StylePreset preset, RoomData room)
    {
        var a = LookDirector.DepthOfFieldFor(preset, 0.8f, 0.5f);
        var b = LookDirector.DepthOfFieldFor(preset, 1.6f, 0.5f);
        var half = LookDirector.Ease(a, b, 0.5f);
        Check(Mathf.IsEqualApprox(half.FarDistance, (a.FarDistance + b.FarDistance) / 2f) && LookDirector.Ease(a, b, 0f) == a && LookDirector.Ease(a, b, 1f).FarDistance == b.FarDistance,
            "Ease moves a band toward another by a fraction, and stays or arrives at its ends");
        var (holder, look) = NewDirector(preset, room, "EaseHolder");
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(0f, 0f, 0.6f);
        var camera = new Camera3D();
        holder.AddChild(camera);
        camera.GlobalPosition = new Vector3(0f, 0.85f, 1.45f);
        camera.LookAt(player.GlobalPosition);
        camera.MakeCurrent();
        look.FocusOverride = new Vector3(0f, 0f, 0.6f);
        await Settle();
        var settledA = ((CameraAttributesPractical)camera.Attributes!).DofBlurFarDistance;
        look.FocusOverride = new Vector3(-1.2f, 0f, -0.9f);
        await Frames(1);
        var afterOne = ((CameraAttributesPractical)camera.Attributes!).DofBlurFarDistance;
        var wanted = LookDirector.DepthOfFieldFor(look.Preset, Distance(camera, look.FocusOverride.Value), Mathf.Max(0f, -(-camera.GlobalBasis.Z).Y)).FarDistance;
        var moved = (afterOne - settledA) / (wanted - settledA);
        Check(wanted - settledA > 0.5f && moved is > 0.02f and < 0.35f, $"when focus jumps across the room the band eases: one frame later it has moved {moved:P0} of the way, not all of it (review minor: focus snapped)");
        await Frames(20);
        var afterTwenty = ((CameraAttributesPractical)camera.Attributes!).DofBlurFarDistance;
        Check(afterTwenty > afterOne && (afterTwenty - settledA) / (wanted - settledA) > 0.6f, "and it keeps arriving: three tenths of a second on it is most of the way");
        await Settle();
        Check(Mathf.Abs(((CameraAttributesPractical)camera.Attributes!).DofBlurFarDistance - wanted) < 0.01f, "and arrives");
        Check(look.Post!.Focus is { } band && Mathf.IsEqualApprox(band.FarDistance, ((CameraAttributesPractical)camera.Attributes!).DofBlurFarDistance), "the focus pass of the post effect works from the same band the camera has");
        holder.QueueFree();
        // Without easing the band snaps; a framed review camera is never eased.
        var snapPreset = PresetWith(Field("focus_ease_s", "0"));
        var (snapHolder, snap) = NewDirector(snapPreset, room, "SnapHolder");
        var snapCamera = new Camera3D();
        snapHolder.AddChild(snapCamera);
        snapCamera.GlobalPosition = new Vector3(0f, 0.85f, 1.45f);
        snapCamera.LookAt(Vector3.Zero);
        snapCamera.MakeCurrent();
        snap.FocusOverride = new Vector3(0f, 0f, 0.6f);
        await Frames(2);
        snap.FocusOverride = new Vector3(-1.2f, 0f, -0.9f);
        await Frames(2);
        var snapWanted = LookDirector.DepthOfFieldFor(snap.Preset, Distance(snapCamera, snap.FocusOverride.Value), Mathf.Max(0f, -(-snapCamera.GlobalBasis.Z).Y)).FarDistance;
        Check(Mathf.Abs(((CameraAttributesPractical)snapCamera.Attributes!).DofBlurFarDistance - snapWanted) < 0.01f, "with focus_ease_s 0 the band snaps, as before");
        snapHolder.QueueFree();
        await Frames(1);
    }

    private async Task CheckObserve(StylePreset preset, RoomData room)
    {
        var t = preset.Tuning.Dof;
        var normal = LookDirector.DepthOfFieldFor(preset, 1.2f, Mathf.Sin(Mathf.DegToRad(40f)));
        var observe = LookDirector.DepthOfFieldFor(preset, 1.2f, Mathf.Sin(Mathf.DegToRad(40f)), observe: true);
        // Observe is the tightest band the look makes: narrower than the miniature tilt-shift at any pitch and than every view's
        // blur at the same depth, and a sliver just wide enough for what it must hold, the whole 10 cm body seen at any pitch
        // (its depth span, plus 3 cm). v1's 6 cm and v2's 14 cm both are; a band widened past the body is not.
        var band = observe.FarDistance - observe.NearDistance;
        var tightest = Enumerable.Range(0, 19).Select(k => LookDirector.DepthOfFieldFor(preset, 1.2f, Mathf.Sin(Mathf.DegToRad(5f * k))))
            .Concat(Enum.GetValues<LookView>().Select(v => LookDirector.DepthOfFieldFor(preset, 1.2f, Mathf.Sin(Mathf.DegToRad(40f)), view: preset.Tuning.BlurFor(v))))
            .Min(d => d.FarDistance - (d.NearEnabled ? d.NearDistance : 0f));
        var bodySpan = 2f * (new Vector2(0.05f, 0.02f).Length() + SmallPlayerController.MaxSeatGapM);
        Check(Mathf.IsEqualApprox(band, t.ObserveBandM, 0.002f) && observe.FarEnabled && observe.NearEnabled && band < 0.5f * tightest && band <= bodySpan + 0.03f,
            $"the observe view's crisp band is a sliver ({band * 100f:0.#} cm: under half the tightest other band, {tightest * 100f:0.#} cm, and at most the body's {bodySpan * 100f:0.#} cm depth plus 3 cm; the normal tilt-shift's here is {(normal.FarDistance - normal.NearDistance) * 100f:0.#} cm)");
        Check(observe.FarTransition <= 0.12f && observe.NearTransition <= 0.1f && observe.FarTransition < normal.FarTransition && observe.Amount >= normal.Amount && Mathf.IsEqualApprox(observe.Amount, t.ObserveAmount),
            "with short ramps and full blur outside it");
        Check(observe.NearDistance < 1.2f && observe.FarDistance > 1.2f, "and the band sits on the focus point");
        var (holder, look) = NewDirector(preset, room, "ObserveHolder");
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(0f, 0f, 0.6f);
        var friend = new Node3D { Name = "Friend" };
        holder.AddChild(friend);
        look.FocusCompanion = friend;
        friend.GlobalPosition = player.GlobalPosition + new Vector3(0.1f, 0f, -0.45f);
        var camera = new Camera3D();
        holder.AddChild(camera);
        camera.GlobalPosition = new Vector3(0f, 0.85f, 1.45f);
        camera.LookAt(player.GlobalPosition);
        camera.MakeCurrent();
        await Settle();
        var normalBand = BandOf(camera);
        look.Observe = true;
        await Settle();
        var observeBand = BandOf(camera);
        Check(observeBand.Far - observeBand.Near < 0.3f * (normalBand.Far - normalBand.Near) && Mathf.Abs(observeBand.Far - observeBand.Near - t.ObserveBandM) < 0.005f,
            $"switching the observe view on narrows the look's band from {(normalBand.Far - normalBand.Near) * 100f:0.#} to {(observeBand.Far - observeBand.Near) * 100f:0.#} cm, and the band no longer stretches to keep the companion in");
        look.FocusOverride = new Vector3(-1f, 0.05f, -0.8f);
        await Settle();
        // Brief 07 (A2): the observe view's focus is the player's rendered body for a third-person camera, ahead of any point
        // set from outside (the HUD's orbit pivot used to steal it and left both avatars soft). The eye camera keeps its rule.
        var middle = player.GetGlobalTransformInterpolated().Origin + Vector3.Up * (player.BodyHeightM * 0.5f);
        Check(InFocus(camera, middle, out _) && !InFocus(camera, look.FocusOverride.Value, out _), "in the observe view a third-person camera stays on the player's body whatever point is set");
        look.Observe = false;
        look.FocusOverride = null;
        await Settle();
        var back = BandOf(camera);
        Check(Mathf.Abs((back.Far - back.Near) - (normalBand.Far - normalBand.Near)) < 0.02f, "switching it off returns the look's normal band");
        holder.QueueFree();
        await Frames(1);
    }

    private static Band BandOf(Camera3D camera)
    {
        var a = (CameraAttributesPractical)camera.Attributes!;
        return new Band(a.DofBlurNearEnabled ? a.DofBlurNearDistance : 0f, a.DofBlurFarDistance);
    }

    // ---------- the focus pass of the post effect: colour contrast as a way to focus ----------

    private void CheckFocusPass(StylePreset preset)
    {
        var post = preset.Tuning.Post;
        var effect = new LookPostEffect { Grain = preset.Grain, Vignette = preset.Vignette, Tuning = post };
        effect.UseProjection(Projection.CreatePerspective(70f, 1.777f, 0.01f, 50f, false));
        var band = LookDirector.DepthOfFieldFor(preset, 1.2f, Mathf.Sin(Mathf.DegToRad(45f)));
        effect.SetFocus(band);
        var constants = effect.PushConstants(new Vector2I(1920, 1080));
        float P(string name) => constants[Array.IndexOf(LookPostEffect.PushConstantNames, name)];
        Check(P("focus_active") == 1f && Mathf.IsEqualApprox(P("far_distance"), band.FarDistance) && Mathf.IsEqualApprox(P("near_distance"), band.NearDistance)
            && Mathf.IsEqualApprox(P("far_transition"), band.FarTransition) && Mathf.IsEqualApprox(P("near_transition"), band.NearTransition)
            && P("focus_saturation") == post.FocusSaturation && P("defocus_saturation") == post.DefocusSaturation && P("bokeh_gain") == post.BokehGain,
            "the effect pushes the camera's depth-of-field band and the preset's focus strengths");
        Check(LookPostEffect.Defocus(constants, (band.NearDistance + band.FarDistance) / 2f) == 0f && LookPostEffect.Defocus(constants, band.FarDistance + band.FarTransition * 2f) == 1f
            && LookPostEffect.Defocus(constants, 0.02f) == 1f && LookPostEffect.Defocus(constants, band.FarDistance + band.FarTransition * 0.5f) is > 0.4f and < 0.6f,
            "defocus is 0 inside the band, 1 well outside it either side, and ramps over the transition");
        var terracotta = new Color(0.55f, 0.26f, 0.18f);
        Color Chroma(Color c) => new(c.R - ColorGrade.Luma(c), c.G - ColorGrade.Luma(c), c.B - ColorGrade.Luma(c));
        var inFocus = LookPostEffect.FocusColor(constants, terracotta, 0f);
        var blurred = LookPostEffect.FocusColor(constants, terracotta, 1f);
        Check(Chroma(inFocus).R > Chroma(terracotta).R && Chroma(blurred).R < Chroma(terracotta).R && Mathf.Abs(ColorGrade.Luma(inFocus) - ColorGrade.Luma(terracotta)) < 0.001f,
            $"in focus a colour is lifted, out of focus drawn back, and its brightness is kept (red over grey {Chroma(terracotta).R:0.000} to {Chroma(inFocus).R:0.000} in focus, {Chroma(blurred).R:0.000} out)");
        var lamp = new Color(3.2f, 2.9f, 2.1f); // a lit bulb: emission well above white
        var highlight = LookPostEffect.FocusColor(constants, lamp, 1f);
        var dim = LookPostEffect.FocusColor(constants, new Color(0.3f, 0.3f, 0.3f), 1f);
        Check(ColorGrade.Luma(highlight) > 1.4f * ColorGrade.Luma(lamp) && ColorGrade.Luma(dim) <= 0.301f && ColorGrade.Luma(LookPostEffect.FocusColor(constants, lamp, 0f)) <= ColorGrade.Luma(lamp) * 1.2f,
            "a highlight in the blurred distance is lifted, so it melts into a creamy bokeh disc; the same highlight in focus and dim things are not");
        // Neutral tuning leaves the pass off.
        var neutral = new LookPostEffect { Tuning = post with { FocusSaturation = 1f, DefocusSaturation = 1f, DefocusDarken = 0f, BokehGain = 0f } };
        neutral.UseProjection(Projection.CreatePerspective(70f, 1.777f, 0.01f, 50f, false));
        neutral.SetFocus(band);
        Check(!LookPostEffect.FocusPassChanges(neutral.Tuning) && neutral.PushConstants(new Vector2I(64, 64))[Array.IndexOf(LookPostEffect.PushConstantNames, "focus_active")] == 0f,
            "a preset with neutral focus strengths leaves the pass off");
        var noBand = new LookPostEffect { Tuning = post };
        noBand.UseProjection(Projection.CreatePerspective(70f, 1.777f, 0.01f, 50f, false));
        Check(noBand.PushConstants(new Vector2I(64, 64))[Array.IndexOf(LookPostEffect.PushConstantNames, "focus_active")] == 0f, "and so does a camera without depth of field");
        Check(LookPostEffect.ComputeSource.Contains("texelFetch(depth_texture, pixel, 0)") && LookPostEffect.ComputeSource.Contains("color.rgb *= vignette * grain;"), "the shader reads the depth buffer, then applies the vignette and grain last");
        // The effect releases its GPU objects safely and as often as asked (review M8's headless half; the freed viewport is a GPU probe).
        effect.Release();
        effect.Release();
        Check(true, "releasing an effect that never ran, twice, is harmless");
        // The cause of the crash was a C# wrapper of the viewport's render buffers outliving the viewport, so the buffers died on
        // the finalizer thread. The GPU probe (free-viewport) proves the fix; this keeps the line that makes it from being removed.
        var source = FileAccess.GetFileAsString("res://scripts/native/Look/LookPostEffect.cs");
        Check(source.Contains("finally") && source.Contains("sceneBuffers?.Dispose();") && !source.Contains("NotificationPredelete") && source.Contains("RenderingServer.CallOnRenderThread"),
            "the effect drops its hold on the viewport's render buffers on the render thread, frees nothing from a destructor, and releases its shader on the render thread (review M8)");
    }

    // ---------- the season looks ----------

    private void CheckSeasonLooks(StylePreset preset)
    {
        float Saturation(int day) => LookClock.At(preset, 12f, day).SeasonSaturation;
        var winter = Saturation(15);
        var spring = Saturation(105);
        var summer = Saturation(196);
        var autumn = Saturation(288);
        Check(winter < 0.7f && spring > 1.1f && autumn > 1.1f && summer > 1.0f && winter < 0.65f * Mathf.Min(spring, autumn),
            $"the season palettes: muted winter ({winter:0.00}), vibrant spring ({spring:0.00}) and autumn ({autumn:0.00}), verdant summer ({summer:0.00})");
        float Noon(int day) { var (rise, set) = LookClock.SunTimes(preset.Tuning.Sun, day); return (rise + set) * 0.5f; }
        var sunWinter = LookClock.At(preset, Noon(15), 15);
        var sunSummer = LookClock.At(preset, Noon(196), 196);
        Check(sunSummer.KeyEnergy > 1.4f * sunWinter.KeyEnergy && sunSummer.Look.SunBlur < 0.7f * sunWinter.Look.SunBlur && sunSummer.SunElevationDeg > sunWinter.SunElevationDeg + 25f,
            $"summer's sun is harder: stronger ({sunSummer.KeyEnergy:0.00} against {sunWinter.KeyEnergy:0.00}), with a crisper shadow edge, and higher");
        var summerTint = sunSummer.SeasonTint;
        var winterTint = sunWinter.SeasonTint;
        Check(summerTint.R > summerTint.B && winterTint.B >= winterTint.R, "summer warm and winter cool in the grade's tint");
        // The key's night energy is not the season's to scale.
        var nightWinter = LookClock.At(preset, 2f, 15).KeyEnergy;
        var nightSummer = LookClock.At(preset, 2f, 196).KeyEnergy;
        Check(Mathf.Abs(nightWinter / nightSummer - 1f) < 0.15f, $"the moon is not the season's to scale ({nightWinter:0.####} in winter, {nightSummer:0.####} in summer, against sun strengths of {sunWinter.Look.SunEnergy:0.0#} and {sunSummer.Look.SunEnergy:0.0#})");
    }

    // ---------- light protections: what a mutation must not get past (review M7) ----------

    private async Task CheckLightProtections(StylePreset preset, RoomData room)
    {
        // The clock follows real time, and is re-read as it goes.
        var savedClock = OS.GetEnvironment("ENFRACTAL_LOOK_CLOCK");
        var savedDate = OS.GetEnvironment("ENFRACTAL_LOOK_DATE");
        OS.UnsetEnvironment("ENFRACTAL_LOOK_CLOCK");
        OS.UnsetEnvironment("ENFRACTAL_LOOK_DATE");
        var real = new DateTime(2026, 4, 15, 5, 40, 0);
        var saved = LookClock.UtcClock;
        try
        {
            // The look's one read of the machine's clock is a UTC instant; `real` is the same moment as this machine's own wall clock reads it.
            LookClock.UtcClock = () => TimeZoneInfo.ConvertTimeToUtc(real, TimeZoneInfo.Local);
            var expected = LookClock.StandardClock(real, TimeZoneInfo.Local, preset.Tuning.Sun.RealClockDaylightSaving);
            Check(preset.FollowClock && preset.FollowCalendar && Mathf.Abs(expected.Hour - preset.DefaultHour) > 3f, "the shipped preset follows the real clock and calendar (and the test's moment is far from its default hour)");
            var (holder, look) = NewDirector(preset, room, "RealClockHolder");
            Check(Mathf.IsEqualApprox(look.Moment.Hour, expected.Hour, 0.001f) && look.Moment.DayOfYear == expected.DayOfYear && look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal),
                $"unpinned, the look shows the real moment: {look.Moment.Hour:0.00} h on day {look.Moment.DayOfYear} ({look.ClockNote})");
            Check(look.Moment.Daylight < 0.5f && look.Moment.KeyElevationDeg > -5f, "at 05:40 in April the light is the dawn's, not the default afternoon's");
            real = new DateTime(2026, 4, 15, 12, 10, 0);
            var noon = LookClock.StandardClock(real, TimeZoneInfo.Local, preset.Tuning.Sun.RealClockDaylightSaving);
            look._Process(4.0);
            Check(Mathf.Abs(look.Moment.Hour - noon.Hour) > 3f, "the real clock is not re-read more often than its interval: after 4 s the moment is unchanged");
            look._Process(7.0);
            Check(Mathf.IsEqualApprox(look.Moment.Hour, noon.Hour, 0.001f), $"after the interval ({LookDirector.ClockUpdateSeconds:0} s) it follows the clock: {look.Moment.Hour:0.00} h");
            // A pinned hour keeps the real calendar; a pinned day keeps the real hour.
            look.SetClock(9f, 100);
            real = new DateTime(2026, 4, 15, 15, 0, 0);
            look._Process(12.0);
            Check(Mathf.IsEqualApprox(look.Moment.Hour, 9f) && look.Moment.DayOfYear == 100, "a pinned clock does not follow the real one");
            look.ReleaseClock();
            Check(look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal), "releasing the pin returns to the real clock");
            holder.QueueFree();
            // The preset's own clock when it does not follow.
            var fixedPreset = PresetWith(("\"follow_clock\": true", "\"follow_clock\": false"), ("\"follow_calendar\": true", "\"follow_calendar\": false"));
            var (fixedHolder, fixedLook) = NewDirector(fixedPreset, room, "FixedClockHolder");
            Check(Mathf.IsEqualApprox(fixedLook.Moment.Hour, fixedPreset.DefaultHour) && fixedLook.Moment.DayOfYear == fixedPreset.Tuning.Seasons.FixedDayOfYear, "a preset that does not follow the clock shows its own hour and day");
            fixedHolder.QueueFree();
        }
        finally
        {
            LookClock.UtcClock = saved;
            if (savedClock.Length > 0) OS.SetEnvironment("ENFRACTAL_LOOK_CLOCK", savedClock);
            if (savedDate.Length > 0) OS.SetEnvironment("ENFRACTAL_LOOK_DATE", savedDate);
        }
        // Lamps switch themselves as the daylight goes, and the sun's shadows come from the shell.
        var (lampHolder, lamps) = NewDirector(preset, room, "LampProtectionHolder");
        lamps.SetClock(12.5f, 196);
        Check(!lamps.LampsOn && lamps.Moment.Daylight > 0.5f, "in the middle of a summer day the lamps are off");
        lamps.SetClock(23f, 196);
        Check(lamps.LampsOn && lamps.Moment.Daylight < 0.25f, "at night they are on");
        lamps.SetLamps(false);
        Check(!lamps.LampsOn && lamps.Lamps.All(l => !l.Visible), "pinned off they stay off at night");
        lamps.SetLamps(true);
        lamps.SetClock(12.5f, 196);
        Check(lamps.LampsOn && lamps.Lamps.All(l => l.Visible), "pinned on they stay on by day");
        lampHolder.QueueFree();
        await Frames(1);
    }
}
