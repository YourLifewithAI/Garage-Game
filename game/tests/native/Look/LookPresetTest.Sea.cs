using Godot;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Fauna;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, the open sea round (Lane L): each view's blur (the island crisp in F1 to F3, the tilt-shift kept in F4 and observe), the
/// endless sea's surface and bed past the room's meshes, the mist that hides the island from the seam out (no distant islands: the
/// sea wraps round to home), and the focus highlight's API.
/// </summary>
public partial class LookPresetTest
{
    /// <summary>
    /// The founder, playing the island garage in F2: "players are going to want to see the landscape a little further out". v1 (locked)
    /// blurs every view as it always did; v2 keeps F1 to F3 crisp across the island and softens only what is far.
    /// </summary>
    private void CheckViewBlur(StylePreset v1, StylePreset v2)
    {
        Check(Enum.GetValues<LookView>().All(v => !v1.Tuning.BlurFor(v).Far) && v1.Tuning.Defaulted.Contains("x_look_views"),
            "v1, written before x_look_views, blurs every view with its own miniature depth of field (left to the code's defaults, which reproduce it)");
        Check(v2.Tuning.BlurFor(LookView.Eye).Far && v2.Tuning.BlurFor(LookView.Shoulder).Far && v2.Tuning.BlurFor(LookView.Diorama).Far && !v2.Tuning.BlurFor(LookView.Isometric).Far,
            "v2: F1 eye, F2 shoulder and F3 diorama soften only what is far; F4 isometric keeps the tilt-shift");
        // F2 as the founder played it: the player 0.32 m ahead, the arm looking about 10 degrees down.
        var down = Mathf.Sin(Mathf.DegToRad(10f));
        var before = LookDirector.DepthOfFieldFor(v2, 0.32f, down);
        var after = LookDirector.DepthOfFieldFor(v2, 0.32f, down, view: v2.Tuning.BlurFor(LookView.Shoulder));
        GD.Print($"LOOK_INFO: F2 depth of field before: crisp {before.NearDistance:0.###} to {before.FarDistance:0.###} m, fully soft from {before.FarDistance + before.FarTransition:0.##} m, amount {before.Amount:0.###}; after: crisp {after.NearDistance:0.###} to {after.FarDistance:0.##} m, fully soft from {after.FarDistance + after.FarTransition:0.#} m, amount {after.Amount:0.###}");
        Check(before.FarDistance + before.FarTransition < 1.2f, $"before, in F2 the island a metre away was already fully soft ({before.FarDistance + before.FarTransition:0.##} m)");
        Check(!after.NearEnabled && after.FarDistance >= 3f && after.FarDistance + after.FarTransition >= 15f, $"after, nothing near is soft and the island is crisp to {after.FarDistance:0.#} m and only the far shore, the distant islands and the horizon soften (fully from {after.FarDistance + after.FarTransition:0.#} m)");
        Check(after.Amount * after.Amount <= before.Amount * before.Amount, $"and the far blur costs no more than before (bokeh amount {after.Amount:0.###} against {before.Amount:0.###})");
        foreach (var view in new[] { LookView.Eye, LookView.Shoulder, LookView.Diorama })
            foreach (var distance in new[] { 0.06f, 0.3f, 1.4f, 2.4f })
                foreach (var pitch in new[] { 0f, 20f, 45f, 80f })
                {
                    var dof = LookDirector.DepthOfFieldFor(v2, distance, Mathf.Sin(Mathf.DegToRad(pitch)), 0.15f, distance * 1.5f, view: v2.Tuning.BlurFor(view));
                    if (!(!dof.NearEnabled && dof.FarDistance > 1.5f * distance + 2.9f && dof.FarTransition > 0f && dof.Amount is > 0f and <= 1f))
                        Check(false, $"a far view keeps everything from the lens to past the subject and the companion crisp, and does not tilt: {view} at {distance} m, {pitch} degrees: {dof}");
                }
        var iso = LookDirector.DepthOfFieldFor(v2, 2.7f, Mathf.Sin(Mathf.DegToRad(RoomHud.IsoPitchDeg)), view: v2.Tuning.BlurFor(LookView.Isometric));
        Check(iso == LookDirector.DepthOfFieldFor(v2, 2.7f, Mathf.Sin(Mathf.DegToRad(RoomHud.IsoPitchDeg))), "F4 keeps exactly the tilt-shift it had");
        var observe = LookDirector.DepthOfFieldFor(v2, 1.4f, 0.7f, observe: true, view: v2.Tuning.BlurFor(LookView.Diorama));
        Check(Mathf.IsEqualApprox(observe.FarDistance - observe.NearDistance, v2.Tuning.Dof.ObserveBandM), "and observe (O) keeps its sliver of focus in any view");
        // The block is read strictly: every view stated, no unknown view, a far view's numbers sensible.
        var text = Encoding.UTF8.GetString(Godot.FileAccess.GetFileAsBytes(StylePreset.PathFor(RoomWorld.DefaultStyleId, 2)));
        StylePreset Variant(string from, string to) => StylePreset.Parse(Encoding.UTF8.GetBytes(text.Replace(from, to)), "variant.json");
        CheckThrows(() => Variant("\"isometric\": { \"blur\": \"miniature\" }", "\"isometric\": { \"blur\": \"tilt\" }"), "is not miniature or far", "an unknown blur style is refused");
        CheckThrows(() => Variant("\"isometric\": { \"blur\": \"miniature\" }", "\"iso\": { \"blur\": \"miniature\" }"), "x_look_views.iso", "an unknown view is refused");
        CheckThrows(() => Variant("\"far_amount\": 0.07 }, \"isometric\"", "\"far_amount\": 0 }, \"isometric\""), "x_look_views.diorama", "a far view without a blur amount is refused");
    }

    /// <summary>The view the director blurs for: what it is told, else the player's eye, else the HUD's view key; a framed camera keeps the miniature.</summary>
    private async Task CheckViewChoice(StylePreset v2, RoomData room)
    {
        var (holder, look) = NewDirector(v2, room, "ViewChoiceHolder");
        look.SetProcess(false);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        look.FocusTarget = player;
        var camera = new Camera3D { Near = 0.01f, Position = new Vector3(0.3f, 0.2f, 1.2f) };
        holder.AddChild(camera);
        camera.MakeCurrent();
        await Frames(1);
        Check(look.ViewFor(player.EyeCamera) == LookView.Eye && look.ViewFor(camera) == null, "unset, the player's eye camera is the eye view and another camera without a HUD keeps the miniature");
        look.View = LookView.Shoulder;
        look._Process(1.0 / 60.0);
        var attributes = (CameraAttributesPractical)camera.Attributes!;
        Check(attributes.DofBlurFarDistance > 3f && look.ViewFor(camera) == LookView.Shoulder, $"told the shoulder view, the game's camera keeps the island crisp ({attributes.DofBlurFarDistance:0.##} m)");
        var review = new Camera3D { Near = 0.01f, Position = new Vector3(0.3f, 0.6f, 1.2f) };
        holder.AddChild(review);
        look.View = null;
        look.FrameCamera(review, Vector3.Zero);
        Check(look.ViewFor(review) == null && ((CameraAttributesPractical)review.Attributes!).DofBlurFarDistance < 2f, "a camera framed for a review keeps the miniature blur unless told a view");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>
    /// The sea mist and the seam (the Glow playtest: the sea wraps round to home, so the island must be wholly hidden, in every view,
    /// where Lane P's wrap moves the swimmer): none at the island, closing in smoothly, and from the seam out nothing of the island
    /// within the island's reach can show to any play camera. The seam is about a minute's swim past the island.
    /// </summary>
    private void CheckSeaMist()
    {
        const float reach = 6f;
        var seam = reach + OpenSea.SeamPastIslandM;
        Check(OpenSea.MistFor(0f, reach) == 0f && OpenSea.MistFor(reach, reach) == 0f && OpenSea.MistFor(seam, reach) == 1f && OpenSea.MistFor(5000f, reach) == 1f,
            "no mist on the island or at its edge; full mist from the seam out, however far");
        var (clearBegin, clearEnd) = OpenSea.MistFog(0f, 100f);
        Check(clearBegin >= 100f - 1e-3f && clearEnd > clearBegin, $"with no mist the fog begins at the far plane ({clearBegin:0.#} m): nothing drawn is touched, so turning it on is no jump");
        var smooth = true;
        var lastMist = 0f;
        var (lastBegin, lastEnd) = OpenSea.MistFog(0f, 100f);
        for (var d = 0f; d <= seam + 5f; d += 0.01f)
        {
            var mist = OpenSea.MistFor(d, reach);
            var (begin, end) = OpenSea.MistFog(mist, 100f);
            smooth &= mist >= lastMist && mist - lastMist < 0.005f && begin <= lastBegin + 1e-4f && end <= lastEnd + 1e-4f && lastEnd - end < 0.6f && begin < end;
            (lastMist, lastBegin, lastEnd) = (mist, begin, end);
        }
        Check(smooth, "swimming out, the mist only thickens and closes in, a little at a time (no jump anywhere)");
        var (fullBegin, fullEnd) = OpenSea.MistFog(1f, 100f);
        Check(Mathf.IsEqualApprox(fullEnd, OpenSea.MistFullM) && Mathf.IsEqualApprox(fullBegin, OpenSea.MistNearM) && fullBegin > 0.5f,
            $"at full mist the swimmer's own water is clear to {fullBegin:0.#} m and everything past {fullEnd:0.#} m is wholly the mist");
        // Every play camera at the seam: the swimmer at the seam on any bearing, the camera anywhere within CameraReachM of it (F1 to F4,
        // F3 orbiting, observe), and any point of the island within its reach: always at least the full mist's distance away.
        var hidden = true;
        for (var a = 0; a < 360; a += 5)
        {
            var swimmer = Vector2.FromAngle(Mathf.DegToRad(a)) * seam;
            foreach (var offset in new[] { Vector2.Zero, new Vector2(OpenSea.CameraReachM, 0f), new Vector2(0f, -OpenSea.CameraReachM), Vector2.FromAngle(Mathf.DegToRad(a + 180)) * OpenSea.CameraReachM })
                hidden &= (swimmer + offset).Length() - reach >= OpenSea.MistFullM - 1e-3f;
        }
        Check(hidden && OpenSea.CameraReachM >= RoomHud.ShoulderFarM + 0.5f && OpenSea.CameraReachM >= RoomHud.IsoDistanceM + 0.5f && OpenSea.CameraReachM >= RoomHud.DioramaMaxDistanceM + 0.5f, $"from the seam ({seam:0.#} m for an island reaching {reach} m) no play camera is nearer the island than {OpenSea.MistFullM} m: wholly hidden in every view");
        var minute = OpenSea.SeamPastIslandM / OpenSea.SwimMps;
        Check(minute is > 45f and < 95f && Mathf.IsEqualApprox(OpenSea.SwimMps, 0.32f * 0.6f), $"the seam is about a minute and a half's swim past the island ({minute:0} s at {OpenSea.SwimMps} m/s)");
    }

    /// <summary>The real island garage, when exported: the surface follows the camera and leaves the room's sea to it, the bed lies under the backdrop's floor with the distant islands laid flat on it, and the mist hides the island from the seam out.</summary>
    private async Task CheckRealOpenSea(StylePreset preset)
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no island garage (-- --landscape=DIR); the open sea's real-room checks were skipped"); return; }
        MaterialLibrary.Configure(preset);
        var land = RoomData.Load(directory);
        if (land.Sea == null) { GD.Print("LOOK_INFO: the exported garage has no sea; the open sea's real-room checks were skipped"); return; }
        var (holder, look) = NewDirector(preset, land, "OpenSeaHolder");
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        await Frames(2);
        var open = look.OpenSea;
        Check(open != null && open.Surface != null && open.Note.Length == 0, "a room with a sea gets the open sea, surface and all: " + (open?.Note ?? "none"));
        if (open?.Surface == null) { holder.QueueFree(); return; }
        var surface = (ShaderMaterial)open.Surface.MaterialOverride!;
        Check(surface.GetShaderParameter("world_pattern").AsBool() && surface.GetShaderParameter("use_hole").AsBool()
            && open.Hole.X <= land.Bounds.Position.X - 20f && open.Hole.Z >= land.Bounds.End.X + 20f,
            $"the surface paints in world space and leaves the room's own sea ({open.Hole}) to it");
        // One sea floor (Codex Astra's review: the look's bed and the bodies' bed were different surfaces): the backdrop's floor and
        // the open bed past it lie at RoomSea.OpenSeaBedAt, the bed swimmers and divers touch, and meet at the floor's rim.
        float Off(MeshInstance3D mesh)
        {
            var vertices = mesh.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            return vertices.Select(v => mesh.GlobalTransform * v).Max(v => Mathf.Abs(v.Y - land.Sea.OpenSeaBedAt(new Vector2(v.X, v.Z))));
        }
        var bedVertices = open.Bed.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var reach = bedVertices.Max(v => new Vector2(v.X, v.Z).DistanceTo(open.Home));
        Check(open.Floors.Count == 1 && Off(open.Floors[0]) < 1e-4f && Off(open.Bed) < 1e-4f && reach >= OpenSea.BedReachM - 1f,
            $"the backdrop's floor and the open bed out to {reach:0} m lie on RoomSea.OpenSeaBedAt (off by at most {Mathf.Max(open.Floors.Count > 0 ? Off(open.Floors[0]) : 1f, Off(open.Bed)) * 1000f:0.###} mm)");
        var floorRim = open.Floors[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(v => open.Floors[0].GlobalTransform * v)
            .Where(v => new Vector2(v.X, v.Z).DistanceTo(open.Home) > 40f).OrderByDescending(v => new Vector2(v.X, v.Z).DistanceTo(open.Home)).Take(8).ToArray();
        Check(floorRim.All(r => bedVertices.Any(b => b.IsEqualApprox(r))), "the open bed carries on from the floor's own rim vertices, so the two meet without a seam");
        var floorMesh = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().First(m => EntityOf(m) == "shell:scenery_moss");
        var floorArrays = floorMesh.Mesh.SurfaceGetArrays(0);
        var floorVertices = floorArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var floorTop = floorArrays[(int)Mesh.ArrayType.Index].AsInt32Array().Select(i => (floorMesh.GlobalTransform * floorVertices[i]).Y).DefaultIfEmpty(float.NaN).Max();
        // No distant islands (the Glow playtest: the horizon is only sea and sky): the generator's six islands lie flat on the bed with
        // the rest of the backdrop's floor, and nothing of the backdrop stands up anywhere.
        var standing = holder.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("DistantIsland")).ToArray();
        Check(standing.Length == 0 && floorTop < land.Sea.LevelM - OpenSea.IslandCutM + 0.25f,
            $"no distant islands: the backdrop's floor, islands and all, lies deep under the water (top {floorTop:0.###} m)");
        // The seam on the real garage: the island's reach (its reef or its room's corners) plus the mist and the cameras' reach.
        var seam = OpenSea.SeamFor(land.Sea, land.Bounds);
        var reef = land.Sea.Reef.Max(p => p.DistanceTo(land.Sea.OpenSeaCentreM));
        GD.Print($"LOOK_INFO: the seam on the island garage: {seam:0.##} m from the island's centre ({land.Sea.OpenSeaCentreM}); the island reaches {open.IslandReachM:0.##} m, the reef {reef:0.##} m; from the reef's farthest point a swim of {(seam - reef) / OpenSea.SwimMps:0} s");
        Check(Mathf.IsEqualApprox(open.SeamM, seam) && open.SeamCentre == land.Sea.OpenSeaCentreM && open.IslandReachM >= reef && seam - reef < 20f,
            $"the open sea's seam ({open.SeamM:0.##} m) is the room's (SeamFor), measured from the open sea's centre, past the reef by a minute or so of swimming");
        // Far out at sea: the surface and bed come along, and the mist follows the swimmer's distance from the island.
        var camera = new Camera3D { Far = 100f };
        holder.AddChild(camera);
        foreach (var at in new[] { new Vector3(0f, 0.1f, -3.9f), new Vector3(40f, 0.05f, -70f), new Vector3(-600f, 0.05f, 900f), new Vector3(8.9f, 0.05f, -6.3f) })
        {
            camera.GlobalPosition = at;
            open.Follow(camera);
            var swimmer = new Vector2(at.X, at.Z);
            var expected = OpenSea.MistFor(swimmer.DistanceTo(open.SeamCentre), open.IslandReachM);
            Check(new Vector2(open.Surface.GlobalPosition.X, open.Surface.GlobalPosition.Z).IsEqualApprox(swimmer) && Mathf.IsEqualApprox(open.Surface.GlobalPosition.Y, land.Sea.LevelM)
                && Mathf.IsEqualApprox(open.Mist, expected) && open.Haze && look.Environment.FogEnabled && open.Fog == ViewDistances.Fog(expected, camera.Far, haze: true),
                $"at {at} the sea's surface is under the swimmer and the mist is {open.Mist:0.##}");
        }
        // At the seam: the look's own environment draws the full mist as depth fog in the horizon's colour, off the sky.
        camera.GlobalPosition = new Vector3(open.SeamCentre.X + open.SeamM, 0.05f, open.SeamCentre.Y);
        open.Follow(camera);
        var env = look.Environment;
        Check(env.FogEnabled && env.FogMode == Godot.Environment.FogModeEnum.Depth && Mathf.IsEqualApprox(env.FogDensity, 1f) && Mathf.IsEqualApprox(env.FogDepthEnd, OpenSea.MistFullM)
            && env.FogSkyAffect == 0f && env.FogSunScatter == 0f && env.FogAerialPerspective == 0f,
            $"at the seam the mist is whole from {env.FogDepthEnd:0.#} m, on everything but the sky");
        camera.GlobalPosition = new Vector3(0.3f, 0.1f, 0.2f);
        open.Follow(camera);
        Check(open.Mist == 0f && env.FogEnabled && Mathf.IsEqualApprox(env.FogDepthBegin, ViewDistances.HazeStartM) && Mathf.IsEqualApprox(env.FogDensity, ViewDistances.HazeMax),
            "back on the island the mist is gone: only the land's soft haze is left, from 40 m (past the whole 1x island)");
        Check(Mathf.IsEqualApprox(surface.GetShaderParameter("horizon_fade_end_m").AsSingle(), 100f * OpenSea.HorizonFadeEnd), "the far sea turns into the horizon just before the camera's far plane");
        // The sea's look: the sea's water (and only water at its level) paints the sea's depths and foam.
        Check(surface.GetShaderParameter("use_sea").AsBool() && Mathf.IsEqualApprox(surface.GetShaderParameter("sea_level").AsSingle(), land.Sea.LevelM)
            && surface.GetShaderParameter("foam_strength").AsSingle() > 0f && surface.GetShaderParameter("sea_deep_m").AsSingle() > 0.5f,
            "the sea's water paints the sea's own depths (the lagoon light, the open sea deep) and its foam");
        // Fish in the sea's deeper water near the swimmer: a fixed number, kept near, never in the playable water, darting from the player.
        var swimmerNode = new Node3D { Name = "Swimmer" };
        holder.AddChild(swimmerNode);
        var life = SeaLife.Create(land.Sea, () => swimmerNode);
        holder.AddChild(life);
        var count = life.Fish.Count;
        foreach (var at in new[] { new Vector3(6f, -0.05f, -12f), new Vector3(60f, -0.05f, -90f), new Vector3(-700f, -0.05f, 400f) })
        {
            swimmerNode.GlobalPosition = at;
            for (var step = 0; step < 30; step++) life.Advance(1f / 30f);
            var placed = life.Anchors.OfType<Vector3>().ToArray();
            Check(life.Fish.Count == count && placed.Length == SeaLife.SchoolCount && placed.All(a => new Vector2(a.X - at.X, a.Z - at.Z).Length() <= SeaLife.KeepWithinM && life.Habitable(new Vector2(a.X, a.Z)))
                && life.Fish.All(f => f.Position.Y < land.Sea.LevelM && f.Position.Y > land.Sea.OpenSeaBedAt(new Vector2(f.Position.X, f.Position.Z))),
                $"at {at} every school swims in deep open water within {SeaLife.KeepWithinM} m of the swimmer, between the bed and the surface ({count} fish)");
        }
        var nearestFish = life.Fish.OrderBy(f => f.Position.DistanceTo(swimmerNode.GlobalPosition)).First();
        swimmerNode.GlobalPosition = nearestFish.Position + new Vector3(0.05f, 0f, 0f);
        var before = nearestFish.Position.DistanceTo(swimmerNode.GlobalPosition);
        for (var step = 0; step < 20; step++) life.Advance(1f / 30f);
        Check(nearestFish.Position.DistanceTo(swimmerNode.GlobalPosition) > before + 0.08f, "a fish darts from the swimmer who comes close (and the Gubble is never one: SeaLife follows the player alone)");
        swimmerNode.GlobalPosition = new Vector3(0f, 0.1f, 0f);
        life.Advance(1f / 30f);
        Check(life.Anchors.All(a => a == null || life.Habitable(new Vector2(a.Value.X, a.Value.Z))), "on the island the fish stay out in the open sea, never in the playable water");
        // The view under the sea: the veil shows only while the camera is under the sea, off the island; a pond's own veil leaves the sea to it.
        camera.GlobalPosition = new Vector3(6f, -0.2f, -12f);
        open.Follow(camera);
        var under = open.Veil.Visible && open.CameraUnderSea;
        camera.GlobalPosition = new Vector3(6f, 0.05f, -12f);
        open.Follow(camera);
        var above = !open.Veil.Visible;
        camera.GlobalPosition = new Vector3(0f, -0.05f, 0f);
        open.Follow(camera);
        var island = !open.Veil.Visible;
        var veilMaterial = (ShaderMaterial)open.Veil.MaterialOverride!;
        Check(under && above && island && veilMaterial.GetShaderParameter("up_light").AsSingle() > 0f && veilMaterial.GetShaderParameter("murk_m").AsSingle() > 0.2f,
            "under the open sea the water tints and hazes the view, brighter toward the surface; above it, or under the island's own ground, there is no veil");
        var pondLife = PondLife.Create(land.RoomId, built).AtOnce();
        pondLife.SeaLevel = land.Sea.LevelM;
        holder.AddChild(pondLife);
        pondLife.SurveyNow(PondSurvey.RayProbe(GetWorld3D().DirectSpaceState));
        Check(pondLife.VeilPondsFor().All(p => Mathf.Abs(p.Level - land.Sea.LevelM) > 0.003f), "the ponds' veil leaves the sea to the open sea's own, so the two never stack");
        look.SetClock(19.5f, 172);
        var horizon = LookSky.At(look.Preset, look.Moment).Horizon;
        Check(surface.GetShaderParameter("horizon_color").AsColor().IsEqualApprox(horizon) && look.Environment.FogLightColor.IsEqualApprox(horizon),
            "the far sea and the mist turn into the sky's own horizon colour of the hour");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>The focus highlight: one thing at a time wears the overlay; switching or clearing gives the meshes back what they had.</summary>
    private async Task CheckFocusHighlight(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "FocusHolder");
        Node3D Thing(string name)
        {
            var thing = new Node3D { Name = name };
            thing.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.03f, 0.03f) } });
            thing.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.01f, BottomRadius = 0.01f, Height = 0.05f } });
            holder.AddChild(thing);
            return thing;
        }
        var crate = Thing("Crate");
        var log = Thing("Log");
        var earlier = new StandardMaterial3D();
        var logMeshes = log.GetChildren().OfType<MeshInstance3D>().ToArray();
        logMeshes[0].MaterialOverlay = earlier;
        look.SetFocusHighlight(crate);
        var crateMeshes = crate.GetChildren().OfType<MeshInstance3D>().ToArray();
        Check(look.Highlight.Target == crate && crateMeshes.All(m => m.MaterialOverlay == look.Highlight.Overlay) && logMeshes.All(m => m.MaterialOverlay != look.Highlight.Overlay),
            "the focus lights every mesh of the one thing in focus, nothing else");
        look.SetFocusHighlight(log);
        Check(crateMeshes.All(m => m.MaterialOverlay == null) && logMeshes.All(m => m.MaterialOverlay == look.Highlight.Overlay), "one thing at a time: moving the focus clears the last one");
        look.SetFocusHighlight(null);
        Check(look.Highlight.Target == null && logMeshes[0].MaterialOverlay == earlier && logMeshes[1].MaterialOverlay == null, "no focus: every mesh gets back the overlay it had");
        look.SetFocusHighlight(crate);
        crate.QueueFree();
        await Frames(2);
        look._Process(1.0 / 60.0);
        Check(look.Highlight.Target == null, "a thing that is freed loses the focus");
        // The overlay chain: a rim on the thing, a light line over a wider dark one; the shaders take what the code sets.
        var under = look.Highlight.Overlay;
        var line = under.NextPass as ShaderMaterial;
        var rim = (line?.NextPass as ShaderMaterial)!;
        Check(line != null && rim == look.Highlight.Rim && under.GetShaderParameter("width_px").AsSingle() > line.GetShaderParameter("width_px").AsSingle()
            && under.GetShaderParameter("line_color").AsColor().Luminance < 0.3f && line.GetShaderParameter("line_color").AsColor().Luminance > 0.8f
            && under.RenderPriority < line.RenderPriority && line.RenderPriority < rim.RenderPriority,
            "the focus is a warm rim over a light line over a wider dark one (it reads on pale sand as on grass)");
        foreach (var material in new[] { rim, line!, under! })
        {
            var declared = material.Shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
            var code = Regex.Replace(material.Shader.Code, @"//[^\n]*", "");
            Check(declared.Count >= 4 && declared.All(n => !material.GetShaderParameter(n).Equals(default(Variant)) || n == "hull_centre") && code.Contains("unshaded") && code.Contains("depth_draw_never"),
                $"{material.ResourceName}: every uniform is set, unlit, and it writes no depth");
        }
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>
    /// The land up close (the founder's F2 playtest): near ground under the shoulder camera read as a smooth, blurred-looking wash because
    /// the role's marks were many pixels wide there. Every landscape role now carries the brush's finer strokes and grain, which come in
    /// only close up (their detail fades with the footprint like every mark).
    /// </summary>
    private void CheckLandUpClose(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var roles = LandscapeLook.Roles.Keys.Where(r => !LandscapeLook.IsWater(r)).ToArray();
        Check(roles.All(r => MaterialLibrary.ForLandscape(r, new Color("c7b8ad"), new Color("66a040")) is ShaderMaterial m && m.GetShaderParameter("close_detail").AsSingle() > 0.5f),
            "every land role wears the brush's finer marks close up");
        var code = Regex.Replace(GD.Load<Shader>(LandscapeLook.ShaderPath).Code, @"//[^\n]*", "");
        Check(Regex.IsMatch(code, @"d_fine\s*=\s*detail\(s\s*\*\s*0\.07,\s*footprint\)\s*\*\s*close_detail") && Regex.IsMatch(code, @"if\s*\(d_fine\s*>\s*0\.0\)"),
            "they fade with the footprint like every mark, and are skipped where they would be finer than a pixel");
    }
}
