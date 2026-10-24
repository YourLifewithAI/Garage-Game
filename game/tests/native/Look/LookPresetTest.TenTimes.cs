using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// S9, the look at 10x: how far you see by rule (the far plane, the aerial haze, the sun's shadows over the island, trees'
/// shadows), the 1x island and the rooms kept as they were, and the look suite's island cameras worked out from the island's data.
/// </summary>
public partial class LookPresetTest
{
    private async Task CheckTenTimes(StylePreset preset, RoomData testRoom)
    {
        CheckViewRules(testRoom);
        CheckTreeShadows();
        await CheckBigOpenLand(preset);
        await CheckRoomKeepsItsView(preset, testRoom);
    }

    /// <summary>The rules alone: the far plane, the shadows and the fog for islands of every size, and the 1x island's numbers unchanged.</summary>
    private void CheckViewRules(RoomData testRoom)
    {
        var small = ViewDistances.FarPlaneForSpan(11f);
        var big = ViewDistances.FarPlaneForSpan(102f);
        Check(ViewDistances.FarPlaneM(testRoom) == 100f && small == 100f && big == ViewDistances.MaxFarM && ViewDistances.MaxFarM == 250f,
            $"the far plane by rule: a room and the 1x island keep 100 m, the 10x island (102 m across) sees {big} m, the plan's 250 m");
        var rising = true;
        for (var span = 0f; span < 400f; span += 0.5f) rising &= ViewDistances.FarPlaneForSpan(span + 0.5f) >= ViewDistances.FarPlaneForSpan(span);
        Check(rising && ViewDistances.FarPlaneForSpan(float.NaN) == 100f, "a bigger island never sees less far, and a broken span falls back to 100 m");

        var before = ViewDistances.OpenLandShadows(11f);
        var after = ViewDistances.OpenLandShadows(102f);
        Check(before == new LandShadows(OpenLandLight.ShadowDistanceM, 2, OpenLandLight.ShadowSplit1, 0f, 0f) && before.DistanceM == 30f && before.Split1 == 0.25f,
            "the 1x island's sun keeps its shadows: 30 m in two splits, the first over a quarter");
        Check(after.DistanceM == 60f && after.Splits == 4 && after.Split1 < after.Split2 && after.Split2 < after.Split3 && after.Split3 < 1f
              && after.DistanceM * after.Split1 <= before.DistanceM * before.Split1,
            $"the 10x island's sun casts shadows over {after.DistanceM} m in four splits, the nearest ({after.DistanceM * after.Split1:0.#} m) as sharp as before or sharper");

        // The fog: off in a room until there is mist; on open land the haze; the mist closes in from it.
        var room = ViewDistances.Fog(0f, 100f, haze: false);
        Check(!room.Enabled && ViewDistances.FogAt(room, 99f) == 0f, "without haze and mist there is no fog");
        var haze = ViewDistances.Fog(0f, big, haze: true);
        Check(haze.Enabled && haze.BeginM == ViewDistances.HazeStartM && haze.EndM == big && haze.Density == ViewDistances.HazeMax && haze.Density < 1f,
            $"open land's haze begins at {haze.BeginM} m and reaches {haze.Density} of the horizon's colour at the far plane ({haze.EndM} m)");
        var reach = ViewDistances.FogAt(haze, 30f);
        var farShore = ViewDistances.FogAt(haze, 100f);
        var fifty = ViewDistances.FogAt(haze, 50f);
        Check(reach == 0f && ViewDistances.FogAt(ViewDistances.Fog(0f, small, true), 12f) == 0f && fifty is > 0.02f and < 0.1f && farShore is > 0.15f and < 0.35f,
            $"nothing within 40 m is hazed (the 1x island lies wholly inside it); from 40 m it shows ({fifty:0.##} at 50 m) and the 10x island's far shore 100 m off takes {farShore:0.##}: a soft haze, not a fog");
        var full = ViewDistances.Fog(1f, big, haze: true);
        Check(full.Enabled && Mathf.IsEqualApprox(full.BeginM, OpenSea.MistNearM) && Mathf.IsEqualApprox(full.EndM, OpenSea.MistFullM) && full.Density == 1f
              && ViewDistances.FogAt(full, OpenSea.MistFullM) == 1f && ViewDistances.FogAt(full, 1.9f) == 0f,
            "at full mist the haze has become the sea mist: clear to 2 m, wholly mist past 8 m");
        var closing = true;
        var last = haze;
        for (var mist = 0.01f; mist <= 1f; mist += 0.01f)
        {
            var fog = ViewDistances.Fog(mist, big, haze: true);
            foreach (var d in new[] { 3f, 8f, 20f, 60f, 150f })
                closing &= ViewDistances.FogAt(fog, d) >= ViewDistances.FogAt(last, d) - 1e-4f && ViewDistances.FogAt(fog, d) - ViewDistances.FogAt(last, d) < 0.2f;
            closing &= fog.BeginM <= last.BeginM && fog.EndM <= last.EndM;
            last = fog;
        }
        Check(closing, "swimming out, the haze thickens into the mist a little at a time at every distance (never thinner, no jump)");
        var mistOnly = ViewDistances.Fog(1f, 100f, haze: false);
        var (mistBegin, mistEnd) = OpenSea.MistFog(1f, 100f);
        Check(mistOnly.Enabled && mistOnly.BeginM == mistBegin && mistOnly.EndM == mistEnd && mistOnly.Density == 1f, "without haze the mist is OpenSea's, as before");
    }

    /// <summary>
    /// The copies' bands (the look's numbers, S9): trees cast shadows across the island's shadow reach; small plants are held in two
    /// bands and tall ones in three, every band contiguous; one- and two-mesh sets keep S6's bands (its own test pins them).
    /// </summary>
    private void CheckTreeShadows()
    {
        static bool Contiguous(CopyBand[] bands, float range) =>
            bands[0].BeginM == 0f && bands.Zip(bands.Skip(1)).All(p => p.First.EndM == p.Second.BeginM) && bands[^1].EndM == range && bands.All(b => b.EndM > b.BeginM);
        var tree = DrawDistances.CopyBands(PlantKind.Tree, 0.55f, 4);
        Check(Contiguous(tree, DrawDistances.TreeM) && tree.Length == 3 && tree.All(b => b.CastsShadow == b.EndM <= DrawDistances.TreeShadowM) && tree[1].EndM == ViewDistances.MaxShadowM
              && tree.Select(b => b.Mesh).SequenceEqual(new[] { 0, 1, 3 }),
            "a tree is held in three bands from the camera to 150 m, full detail near, casting shadows to the island's 60 m shadow reach: " + string.Join("; ", tree));
        var grass = DrawDistances.CopyBands(PlantKind.Plant, 0.032f, 4);
        Check(Contiguous(grass, DrawDistances.SmallPlantM) && grass.Length == 2 && grass[0] == new CopyBand(0, 0f, DrawDistances.SmallDetailM, true) && grass[1].Mesh == 3 && !grass[1].CastsShadow,
            "a small plant (3 cm grass) is held in two bands: full detail with its shadow to 8 m, its lightest mesh beyond to 40 m: " + string.Join("; ", grass));
        var shrub = DrawDistances.CopyBands(PlantKind.Plant, 0.12f, 3);
        Check(Contiguous(shrub, DrawDistances.PlantM) && shrub.Length == 3 && shrub.All(b => b.CastsShadow == b.EndM <= DrawDistances.CopyShadowM),
            "a taller plant (a 12 cm shrub) is held in three bands, casting shadows to 20 m: " + string.Join("; ", shrub));
        var twoMesh = DrawDistances.CopyBands(PlantKind.Plant, 0.12f, 2);
        Check(twoMesh.Length == 3 && twoMesh.Select(b => b.Mesh).SequenceEqual(new[] { 0, 1, 1 }) && twoMesh.All(b => b.CastsShadow == b.EndM <= DrawDistances.CopyShadowM),
            "a two-mesh plant keeps S6's bands");
        // The 10x garage's sets (kind, copies, typical height, meshes): its copies are held about 716,000 times instead of 1.4 million.
        var sets = new (PlantKind Kind, int Count, float Height, int Meshes)[]
        {
            (PlantKind.Tree, 2152, 0.517f, 4), (PlantKind.Plant, 15497, 0.028f, 4), (PlantKind.Tree, 2698, 0.604f, 4), (PlantKind.Plant, 32376, 0.029f, 4),
            (PlantKind.Plant, 9961, 0.044f, 4), (PlantKind.Plant, 246694, 0.032f, 4), (PlantKind.Plant, 13983, 0.023f, 4), (PlantKind.Plant, 225, 0.066f, 4),
            (PlantKind.Stone, 1079, 0.035f, 3), (PlantKind.Plant, 11159, 0.124f, 3), (PlantKind.Plant, 13831, 0.032f, 4),
        };
        var held = sets.Sum(s => (long)s.Count * DrawDistances.CopyBands(s.Kind, s.Height, s.Meshes).Length);
        Check(held < 750_000, $"the 10x garage's 349,655 copies are held {held:N0} times in their bands (S6's bands: 1,403,470)");
    }

    /// <summary>A big open land (the test room's floor 25 times wider, no sea): the far plane, the haze and the shadows the rules give it, and the game's camera kept at that far plane.</summary>
    private async Task CheckBigOpenLand(StylePreset preset)
    {
        var directory = LookFixtureRooms.Write("big_open_land", room =>
        {
            var parts = room["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["role"]!.GetValue<string>() != "floor").ToArray()) parts.Remove(part);
            parts[0]!["role"] = "ground";
            foreach (var point in parts[0]!["geometry"]!["points_m"]!.AsArray())
            {
                point![0] = point[0]!.GetValue<double>() * 25;
                point[2] = point[2]!.GetValue<double>() * 25;
            }
            foreach (var corner in new[] { "min_m", "max_m" })
            {
                var c = room["bounds"]![corner]!.AsArray();
                c[0] = c[0]!.GetValue<double>() * 25;
                c[2] = c[2]!.GetValue<double>() * 25;
            }
            room["shell"]!["openings"] = new JsonArray();
            LookFixtureRooms.RemoveSunHint(room);
            var hints = LookFixtureRooms.Hints(room);
            foreach (var hint in hints.ToArray()) hints.Remove(hint);
        });
        var land = RoomData.Load(directory);
        var (holder, look) = NewDirector(preset, land, "BigOpenLandHolder");
        look.SetClock(12f, 172);
        var span = ViewDistances.SpanM(land);
        Check(look.OpenLand && span > 100f && look.FarPlaneM == 250f, $"a {span:0} m open land sees to {look.FarPlaneM} m");
        Check(look.Key.DirectionalShadowMaxDistance == 60f && look.Key.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits
              && Mathf.IsEqualApprox(look.Key.DirectionalShadowSplit1, ViewDistances.FourSplit1) && Mathf.IsEqualApprox(look.Key.DirectionalShadowSplit3, ViewDistances.FourSplit3),
            $"its sun casts shadows over {look.Key.DirectionalShadowMaxDistance} m in four splits");
        var env = look.Environment;
        Check(env.FogEnabled && env.FogMode == Godot.Environment.FogModeEnum.Depth && Mathf.IsEqualApprox(env.FogDepthBegin, ViewDistances.HazeStartM)
              && Mathf.IsEqualApprox(env.FogDepthEnd, 250f) && Mathf.IsEqualApprox(env.FogDensity, ViewDistances.HazeMax) && env.FogSkyAffect == 0f && env.FogLightEnergy > 0f,
            "with no sea the look draws the haze itself: from 40 m to the far plane, in the horizon's colour, never on the sky");
        // The game's camera (a HUD view made at 100 m) is kept at the far plane; a camera the harness framed keeps its own.
        var camera = new Camera3D { Far = 100f };
        holder.AddChild(camera);
        camera.MakeCurrent();
        var framed = new Camera3D { Far = 50f };
        holder.AddChild(framed);
        look.FrameCamera(framed, Vector3.Zero);
        await Frames(2);
        Check(camera.Far == 250f && framed.Far == 50f, $"the current camera sees to the far plane ({camera.Far} m); a framed review camera keeps its own ({framed.Far} m)");
        camera.ClearCurrent(false);
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>The test room keeps its view: 100 m, no fog, its shadows as the preset sets them.</summary>
    private async Task CheckRoomKeepsItsView(StylePreset preset, RoomData testRoom)
    {
        var (holder, look) = NewDirector(preset, testRoom, "RoomViewHolder");
        var s = preset.Tuning.Shadows;
        Check(look.FarPlaneM == 100f && !look.Environment.FogEnabled
              && Mathf.IsEqualApprox(look.Key.DirectionalShadowSplit1, s.KeySplit1)
              && Mathf.IsEqualApprox(look.Key.DirectionalShadowMaxDistance, Mathf.Max(s.KeyMinDistanceM, testRoom.Bounds.Size.Length() * s.KeyDistancePerDiagonal)),
            "the test room keeps its view: a 100 m far plane, no haze, its shadows as the preset sets them");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>The look suite's island cameras worked out from the 1x garage's data: each stands on land where the data says.</summary>
    private void CheckIslandCameras(RoomData land, Node3D built)
    {
        var space = built.GetWorld3D().DirectSpaceState;
        var ground = IslandCameras.RayGround(space, land.Bounds);
        var notes = new List<string>();
        var cameras = IslandCameras.Derive(land, ground, IslandCameras.RaySky(space), notes);
        GD.Print("LOOK_INFO: the 1x garage's island cameras: " + string.Join("; ", cameras.Select(c => $"{c.Id} at {c.PlayerM} facing {c.YawDeg:0} degrees"))
                 + (notes.Count > 0 ? "; notes: " + string.Join("; ", notes) : ""));
        Check(cameras.Select(c => c.Id).SequenceEqual(IslandCameras.Ids), "the 1x garage has every island camera: " + string.Join(", ", cameras.Select(c => c.Id)));
        DerivedCamera Get(string id) => cameras.First(c => c.Id == id);
        var spawn = land.SpawnFor("player").PositionM;
        var middle = IslandCameras.Middle(land);
        Check(Get("spawn").PlayerM == spawn && Mathf.Abs(Mathf.AngleDifference(Mathf.DegToRad(Get("spawn").YawDeg), Mathf.Atan2(-(middle.X - spawn.X), -(middle.Y - spawn.Z)))) < 0.01f,
            "the spawn camera stands at the player's spawn, facing the island's middle");
        var samples = IslandCameras.SampleLand(land, ground);
        var summit = Get("summit").PlayerM;
        Check(samples.Count > 500 && samples.Count(s => s.Y > summit.Y + 1e-4f) < 0.1f * samples.Count && IslandCameras.RaySky(space)(summit) && IslandCameras.OnLand(land, new Vector2(summit.X, summit.Z)),
            $"the summit camera stands on the island's high ground ({summit.Y:0.###} m; {samples.Count(s => s.Y > summit.Y + 1e-4f)} of {samples.Count} ground samples higher) under open sky");
        var crowns = IslandCameras.Crowns(land);
        Check(crowns.Count == land.Copies!.Sets.Where(s => s.Kind == PlantKind.Tree).Sum(s => s.Count) && crowns.All(c => c.RadiusM > 0.02f)
              && !crowns.Any(c => c.Centre.DistanceTo(new Vector2(summit.X, summit.Z)) < c.RadiusM + IslandCameras.SummitClearM),
            $"no tree's crown ({crowns.Count} on the island) overhangs the summit camera or its shoulder camera");
        var structures = land.Objects.Where(o => o.Asset.CategoryGroup == "structure").Select(o => new Vector2(o.PositionM.X, o.PositionM.Z)).ToArray();
        var hamlet = new Vector2(Get("hamlet").PlayerM.X, Get("hamlet").PlayerM.Z);
        Check(structures.Length >= 2 && structures.Count(s => s.DistanceTo(hamlet) < IslandCameras.StructureClusterM + IslandCameras.HamletStandOffM) >= 2 && ground(hamlet) != null,
            $"the hamlet camera stands on the ground beside the cottages ({structures.Count(s => s.DistanceTo(hamlet) < 2.5f)} within 2.5 m)");
        var beach = land.Sea!.NearestBeach(new Vector2(spawn.X, spawn.Z))!.Value;
        Check(new Vector2(Get("beach").PlayerM.X, Get("beach").PlayerM.Z).DistanceTo(new Vector2(beach.WashAshoreM.X, beach.WashAshoreM.Z)) < 0.01f,
            $"the beach camera stands on the beach nearest the spawn ({beach.Id})");
        var flight = Get("flight").Flight!.Value;
        Check(flight.FromM.Y > summit.Y && flight.FromM.DistanceTo(flight.ToM) > 1f, "the flight crosses the island above its summit");
    }
}
