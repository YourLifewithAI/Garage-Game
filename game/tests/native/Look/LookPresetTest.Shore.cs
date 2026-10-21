using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The living shore (Run 2, the first recorded play's mark 2: "the coastline is a perfect line where the water meets the land; this
/// should be dynamic, with waves gently washing against the shore"; decision 66). Where the sea meets the land a wash climbs the
/// sand and draws back every few seconds, the sand stays damp where it has been, and the generator's coast stays exactly as it is:
/// the water shader moves the sea's edge, the land shader paints the wash and the damp band, both from one function
/// (shore_wash.gdshaderinc, which ShoreWash mirrors). Headless: the mirror's numbers, the shaders' contract and the real island's
/// vertices; what it looks like is the captures' business.
/// </summary>
public partial class LookPresetTest
{
    private static readonly ShoreLook Shore = ShoreLook.Default;

    private async Task CheckShore(StylePreset preset)
    {
        CheckShoreWash();
        CheckShoreOnLand();
        CheckShoreShaders(preset);
        await CheckShoreOnASyntheticSea(preset);
        await CheckShoreOnTheIsland(preset);
    }

    /// <summary>The wash itself, from the mirror: slow, periodic, moving with the clock, never a ruler's line, never in unison, never surf.</summary>
    private void CheckShoreWash()
    {
        var at = new Vector2(9.45f, -34.8f);
        // The wash moves with the clock: it rises through the first share of the period and falls through the rest.
        var swing = Enumerable.Range(0, 80).Select(i => ShoreWash.Level(at, i * 0.1f, Shore).Level).ToArray();
        var changes = Enumerable.Range(1, swing.Length - 1).Count(i => MathF.Abs(swing[i] - swing[i - 1]) > 1e-5f);
        Check(swing.Max() - swing.Min() > 0.05f && changes > 60 && ShoreWash.Level(at, 1f, Shore).Level != ShoreWash.Level(at, 1.5f, Shore).Level,
            $"the wash moves with the clock: over one period it stands from {swing.Min() * 100f:0.0} to {swing.Max() * 100f:0.0} cm over the sea's level, and the level changes at nearly every step of it ({changes} of 79)");
        // One place over six periods: the level stays between the draw-back and the run-up, and touches both ends every period.
        var lowest = new List<float>();
        var highest = new List<float>();
        var maxSpeed = 0f;
        for (var wave = 0; wave < 6; wave++)
        {
            float low = float.PositiveInfinity, high = float.NegativeInfinity, last = float.NaN;
            for (var step = 0; step <= 400; step++)
            {
                var clock = (wave + step / 400f) * Shore.PeriodS;
                var level = ShoreWash.Level(at, clock, Shore).Level;
                low = MathF.Min(low, level); high = MathF.Max(high, level);
                if (!float.IsNaN(last)) maxSpeed = MathF.Max(maxSpeed, MathF.Abs(level - last) / (Shore.PeriodS / 400f));
                last = level;
            }
            lowest.Add(low); highest.Add(high);
        }
        Check(lowest.All(l => l >= -Shore.DrawBackM - 1e-4f && l < -Shore.DrawBackM + 0.004f) && highest.All(h => h <= Shore.RunUpM + 1e-4f && h >= ShoreWash.SizeLow * Shore.RunUpM - 1e-4f),
            $"every period the wash draws back to {Shore.DrawBackM * 100f:0} cm under the sea's level and runs up to between {ShoreWash.SizeLow * Shore.RunUpM * 100f:0.0} and {Shore.RunUpM * 100f:0.0} cm over it (here {highest.Min() * 100f:0.0} to {highest.Max() * 100f:0.0})");
        Check(highest.Max() - highest.Min() > 0.002f, $"each wave has its own height (crests {string.Join(", ", highest.Select(h => (h * 100f).ToString("0.0", CultureInfo.InvariantCulture)))} cm): no two washes alike");
        // Calm: the edge never moves faster than the body walks (0.32 m/s) on the garage's beaches (a slope of 0.16 above the sea).
        const float beachSlope = 0.16f;
        GD.Print($"LOOK_INFO: the wash's edge at its fastest: {maxSpeed * 100f:0.0} cm/s of height, {maxSpeed / beachSlope * 100f:0.0} cm/s along a beach of slope {beachSlope}; a wash every {Shore.PeriodS:0.#} s, up {Shore.RiseShare * Shore.PeriodS:0.0} s and back {(1f - Shore.RiseShare) * Shore.PeriodS:0.0} s");
        Check(maxSpeed / beachSlope < 0.32f && maxSpeed < 0.05f, $"calm: the edge moves at most {maxSpeed * 100f:0.0} cm/s up and down, {maxSpeed / beachSlope * 100f:0.0} cm/s along a beach (slower than the body's 32 cm/s walk)");
        // Slow: a wash takes seconds to climb, and longer to draw back.
        Check(Shore.PeriodS >= 5f && Shore.RiseShare * Shore.PeriodS >= 2f && (1f - Shore.RiseShare) > Shore.RiseShare, "slow: a wash every few seconds, the climb at least two seconds, the draw-back longer than the climb");
        // Never a ruler's line: along ten metres of shore at one instant the edge is not at one height, and it is smooth (no step between 2 cm neighbours).
        var along = Enumerable.Range(0, 500).Select(i => ShoreWash.Level(new Vector2(i * 0.02f, -34.8f), 5.3f, Shore).Level).ToArray();
        var spread = along.Max() - along.Min();
        var steps = Enumerable.Range(1, along.Length - 1).Max(i => MathF.Abs(along[i] - along[i - 1]));
        Check(spread > 0.02f && steps < 0.01f, $"the edge is never a ruler's line: along 10 m of shore at one moment it varies by {spread * 100f:0.0} cm, and by at most {steps * 1000f:0.0} mm between neighbours 2 cm apart");
        // Never in unison: two stretches of shore a few metres apart are on their own time.
        var apart = new[] { 2.6f, 3.9f, 5.2f, 6.5f }.Max(d => Enumerable.Range(0, 400).Max(i => MathF.Abs(ShoreWash.Level(at, i * 0.05f, Shore).Level - ShoreWash.Level(at + new Vector2(d, 0f), i * 0.05f, Shore).Level)));
        Check(apart > 0.02f, $"stretches of shore a few metres apart keep their own time: the wash differs by up to {apart * 100f:0.0} cm between them");
        // The damp band's top is smooth along the shore and never under a crest (with the band above it).
        var damps = Enumerable.Range(0, 400).Select(i => ShoreWash.Level(new Vector2(i * 0.05f, 3f), 0f, Shore).Damp).ToArray();
        var worstCrestOver = Enumerable.Range(0, 400).Max(i => ShoreWash.Level(at + new Vector2(i * 0.05f, 0f), 7.7f, Shore).Level - (ShoreWash.Level(at + new Vector2(i * 0.05f, 0f), 7.7f, Shore).Damp + Shore.WetAboveM));
        Check(damps.All(d => d >= ShoreWash.DampLow * Shore.RunUpM - 1e-5f && d <= Shore.RunUpM + 1e-5f) && worstCrestOver < 0f,
            $"the damp band's top lies between {ShoreWash.DampLow * Shore.RunUpM * 100f:0.0} and {Shore.RunUpM * 100f:0.0} cm over the sea's level, and the wash never stands above it");
        // The mirror and the include agree on every constant (the include is the shaders' one source of the wash).
        var text = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(ShoreWash.IncludePath));
        string F(float v) => v.ToString("0.0#", CultureInfo.InvariantCulture);
        Check(text.Contains($"shore_noise(xz / lane_m) * {F(ShoreWash.LaneSpread)};") && text.Contains($"mix({F(ShoreWash.SizeLow)}, 1.0, shore_noise(xz / (lane_m * 1.7)")
            && text.Contains($"mix({F(ShoreWash.DampLow)}, 1.0, shore_noise(xz / (lane_m * 2.3)") && text.Contains("vec2(wave * 7.31, wave * 3.17)") && text.Contains("vec2(19.7, 4.3)")
            && text.Contains("phase < rise_share ? rise : 1.0 - fall") && text.Contains("return vec2(mix(-draw_back_m, crest, swash), damp);"),
            "the mirror's constants are the include's: lane spread, wave height, damp band, the per-wave and per-band offsets and the swash's shape");
    }

    /// <summary>The land's side, from the mirror: damp and wash only on a sloping beach within the named reach of the coast, nothing past it.</summary>
    private void CheckShoreOnLand()
    {
        var at = new Vector2(3.1f, -34.4f);
        var beach = 0.16f;
        // Nothing changes above the beach (the band's top plus its margin), at any moment, anywhere along the shore.
        var anyAbove = false;
        var anyBelow = false;
        for (var i = 0; i < 400; i++)
        {
            var xz = at + new Vector2(i * 0.07f, 0f);
            for (var clock = 0f; clock < 3f * Shore.PeriodS; clock += 0.37f)
            {
                var top = Shore.RunUpM + Shore.WetAboveM + ShoreWash.BandAboveM + 0.001f;
                var (wet, film) = ShoreWash.OnLand(top, xz, clock, 0.1f, beach, Shore);
                anyAbove |= wet > 0f || film > 0f;
                var (wetDeep, filmDeep) = ShoreWash.OnLand(-Shore.DrawBackM - ShoreWash.BandBelowM - 0.001f, xz, clock, 0.1f, beach, Shore);
                anyBelow |= wetDeep > 0f || filmDeep > 0f;
            }
        }
        Check(!anyAbove && !anyBelow, $"nothing changes above the beach (more than {(Shore.RunUpM + Shore.WetAboveM + ShoreWash.BandAboveM) * 100f:0.0} cm over the sea's level) nor on the bed deeper than {(Shore.DrawBackM + ShoreWash.BandBelowM) * 100f:0} cm under it: no damp, no wash, at any moment");
        // Past the reach of the coast, or on a flat, nothing changes either, however low the ground lies.
        var far = 0f;
        var flat = 0f;
        for (var i = 0; i < 400; i++)
        {
            var xz = at + new Vector2(i * 0.05f, 1f);
            for (var clock = 0f; clock < 2f * Shore.PeriodS; clock += 0.5f)
                for (var rel = -0.03f; rel <= 0.05f; rel += 0.005f)
                {
                    var (wetFar, filmFar) = ShoreWash.OnLand(rel, xz, clock, Shore.ReachM + 0.01f, beach, Shore);
                    far = MathF.Max(far, MathF.Max(wetFar, filmFar));
                    var (wetFlat, filmFlat) = ShoreWash.OnLand(rel, xz, clock, 0.1f, 0f, Shore);
                    flat = MathF.Max(flat, MathF.Max(wetFlat, filmFlat));
                }
        }
        Check(far == 0f && flat == 0f, $"past the reach of the coast ({Shore.ReachM:0.0} m) and on a flat, however low it lies, the land is exactly as it was (largest change {far:0.###} and {flat:0.###})");
        // On the beach, near the coast: the ground just over the sea's level is damp at every moment, and wash covers it at the top of a wash and not at the bottom.
        var dampAlways = true;
        float wettest = 0f, deepest = 0f, driest = float.PositiveInfinity;
        for (var clock = 0f; clock < 4f * Shore.PeriodS; clock += 0.1f)
        {
            var (wet, film) = ShoreWash.OnLand(0.004f, at, clock, 0.2f, beach, Shore);
            dampAlways &= wet > 0.5f;
            wettest = MathF.Max(wettest, wet);
            deepest = MathF.Max(deepest, film);
            driest = MathF.Min(driest, film);
        }
        Check(dampAlways && deepest > 0.005f && driest == 0f, $"on a beach beside the sea the sand just over its level stays damp all the time (down to {wettest:0.##} of the full) and the wash covers it, up to {deepest * 100f:0.0} cm deep, only while a wash is up ({driest * 100f:0} cm when drawn back)");
        // The wet band's width: on the garage's beaches the damp band is as wide as the run-up allows and no wider than the named reach.
        var bandM = (Shore.RunUpM + Shore.WetAboveM) / beach;
        Check(bandM < Shore.ReachM && Shore.ReachM <= 1.0f, $"the damp band reaches {bandM:0.00} m up a beach of slope {beach} from the sea's level, within the named reach of {Shore.ReachM:0.0} m from the coast");
        // Water: where the sea has drawn back it is shallower, and nothing changes in the deep (the shader only works the shallows).
        var shallower = ShoreWash.WaterDepth(0.01f, at, 0f, Shore);
        var level0 = ShoreWash.Level(at, 0f, Shore).Level;
        Check(MathF.Abs(shallower - (0.01f + level0)) < Shore.BrushAmpM, "the sea's depth under the wash is its depth plus where the wash stands (less when it has drawn back)");
    }

    /// <summary>The shaders' contract: one shared function, guarded, off by default, nothing in the deep sea or on the flat inland.</summary>
    private void CheckShoreShaders(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        string Clean(string path) => Regex.Replace(Regex.Replace(GD.Load<Shader>(path).Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        var water = Clean(LandscapeLook.WaterShaderPath);
        var land = Clean(LandscapeLook.ShaderPath);
        Check(GD.Load<Shader>(LandscapeLook.WaterShaderPath).Code.Contains($"#include \"{ShoreWash.IncludePath}\"") && GD.Load<Shader>(LandscapeLook.ShaderPath).Code.Contains($"#include \"{ShoreWash.IncludePath}\""),
            "the water and the land shaders include the one wash (shore_wash.gdshaderinc)");
        var bandAt = land.IndexOf("if (rel > -shore_draw_back_m - 0.04 && rel < shore_run_up_m + shore_wet_above_m + 0.01)", StringComparison.Ordinal);
        Check(water.Contains("if (sea && shore_on && intro_rise.w < 0.5 && FRONT_FACING && depth_m < shore_draw_back_m + 0.03)") && water.IndexOf("shore_level(", water.IndexOf("if (sea && shore_on && intro_rise.w < 0.5", StringComparison.Ordinal), StringComparison.Ordinal) > 0
            && land.Contains("if (shore_on && intro_rise.w < 0.5)") && bandAt > 0 && land.IndexOf("textureLod(shore_field", StringComparison.Ordinal) > bandAt,
            "the work is guarded: the water only where the sea is shallow, the land only in the beach's band of heights (the field is read inside it), and not while the intro plays");
        Check(Regex.IsMatch(land, @"gate\s*=\s*\(1\.0\s*-\s*smoothstep\(0\.7\s*\*\s*shore_reach_m,\s*shore_reach_m,\s*coast_m\)\)\s*\*\s*smoothstep\(shore_flat_slope") && Regex.IsMatch(land, @"if\s*\(gate\s*>\s*0\.0\)"),
            "the land changes only within the reach of the coast and on ground that slopes (the field's distance and the slope, then nothing else is done)");
        Check(Regex.IsMatch(water, @"alpha\s*=\s*max\(alpha\s*\*\s*edge_water,\s*edge_lace\s*\*\s*0\.95\)"), "the water thins to nothing where the wash has drawn back, and keeps its lace of foam");
        Check(water.Contains("shore_hold_s >= 0.0 ? shore_hold_s : TIME") && land.Contains("shore_hold_s >= 0.0 ? shore_hold_s : TIME") && !water.Contains("shore_clock") && !land.Contains("shore_clock"),
            "both shaders run the wash on the engine's TIME (nothing is set on a material each frame), unless a capture holds it at a second");
        // Off by default: a pond, a brook and a room without a sea are painted exactly as before.
        var pond = (ShaderMaterial)MaterialLibrary.ForLandscape("still_water", new Color("619baf"), new Color("3a6f80"));
        var gravel = (ShaderMaterial)MaterialLibrary.ForLandscape("gravel", new Color("c7ba9e"), new Color("c7ba9e"));
        Check(!pond.GetShaderParameter("shore_on").AsBool() && !gravel.GetShaderParameter("shore_on").AsBool() && gravel.GetShaderParameter("shore_field").AsGodotObject() is Texture2D { } empty && empty.GetWidth() == 1,
            "ponds, brooks and every land material start with the shore off (and the one-texel field), so a room without a sea is untouched");
        // (shore_m and shore_strength are the ponds' older pale wet line, which the sea's edge now follows, not the living shore's.)
        var declaredWater = GD.Load<Shader>(LandscapeLook.WaterShaderPath).GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).Where(n => n.StartsWith("shore_") && n is not ("shore_m" or "shore_strength")).ToHashSet();
        var declaredLand = GD.Load<Shader>(LandscapeLook.ShaderPath).GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).Where(n => n.StartsWith("shore_")).ToHashSet();
        var setWater = ShoreWash.WaterParameters(Shore, false).Select(p => p.Name.ToString()).ToHashSet();
        var setLand = ShoreWash.LandParameters(Shore, false, ShoreWash.FreeRunning, 0f, ShoreWash.DefaultFilmColour, SeaLook.Default.FoamColor, null).Select(p => p.Name.ToString()).ToHashSet();
        Check(declaredWater.SetEquals(setWater) && declaredLand.SetEquals(setLand) && setWater.Count == 15 && setLand.Count == 28,
            $"the shore's uniforms are the ones the shaders declare, none missing and none unread ({setWater.Count} for the water, {setLand.Count} for the land): " + string.Join(",", declaredWater.Except(setWater).Concat(setWater.Except(declaredWater)).Concat(declaredLand.Except(setLand)).Concat(setLand.Except(declaredLand))));
    }

    /// <summary>The wiring, on a synthetic sea: the field is each place's distance to the coast, the clock moves the wash, the shore leaves with the sea.</summary>
    private async Task CheckShoreOnASyntheticSea(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var sea = SyntheticSea();
        var holder = new Node3D { Name = "SyntheticShoreHolder" };
        AddChild(holder);
        var water = SyntheticSeaSurface(holder);
        var backdrop = SyntheticBackdrop(holder);
        var open = OpenSea.Build(sea, new Aabb(new Vector3(-2f, -1f, -2f), new Vector3(4f, 3f, 4f)), new[] { water }, new[] { backdrop });
        holder.AddChild(open);
        await Frames(1);
        var sw = Stopwatch.StartNew();
        var field = ShoreWash.BuildField(sea, Shore);
        sw.Stop();
        Check(field != null && open.ShoreField != null, "a room with a coast has a shore field");
        if (field == null) { holder.QueueFree(); await Frames(1); return; }
        // The field is each place's distance to the coast's nearest edge, to a texel and the encoding's step, up to its range; the rest is far.
        var worst = 0f;
        var rng = new RandomNumberGenerator { Seed = 7 };
        for (var i = 0; i < 400; i++)
        {
            var p = new Vector2(rng.RandfRange(-3f, 3f), rng.RandfRange(-3f, 3f));
            var expected = MathF.Min(RoomSea.Nearest(sea.Coast, p).Distance, field.RangeM);
            worst = MathF.Max(worst, MathF.Abs(field.DistanceAt(p) - expected));
        }
        Check(worst < field.CellM + field.RangeM / 255f, $"the field holds each place's distance to the coast, within a texel ({worst * 100f:0.0} cm at worst; built in {sw.Elapsed.TotalMilliseconds:0} ms)");
        GD.Print($"LOOK_INFO: shore field samples: coast {field.DistanceAt(new Vector2(1f, 0f)):0.####}, middle {field.DistanceAt(new Vector2(0f, 0f)):0.####}, far {field.DistanceAt(new Vector2(30f, 30f)):0.####} (range {field.RangeM}, cell {field.CellM}, area {field.Area}, {field.Width}x{field.Depth})");
        Check(field.DistanceAt(new Vector2(1f, 0f)) < 0.07f && MathF.Abs(field.DistanceAt(new Vector2(0f, 0f)) - 1f) < 0.08f && field.DistanceAt(new Vector2(30f, 30f)) >= field.RangeM - 0.01f,
            "it reads 0 on the coast, a metre in the middle of a two-metre island and its whole range (far) beyond its square");
        // The wiring: the sea's water and the backdrop's land are on the shore; the clock moves every one of them.
        var waters = open.Surface?.MaterialOverride is ShaderMaterial surface ? new[] { surface, (ShaderMaterial)water.GetSurfaceOverrideMaterial(0) } : Array.Empty<ShaderMaterial>();
        Check(waters.Length == 2 && waters.All(m => m.GetShaderParameter("shore_on").AsBool()) && open.ShoreLandMaterials.Count >= 1
            && open.ShoreLandMaterials.All(m => m.GetShaderParameter("shore_on").AsBool() && Mathf.IsEqualApprox(m.GetShaderParameter("shore_sea_level").AsSingle(), sea.LevelM)
            && ReferenceEquals(m.GetShaderParameter("shore_field").AsGodotObject(), open.ShoreField!.Texture)),
            "the sea's water and the land's materials are on the shore, at the sea's level, with the field");
        // The wash runs on the engine's own time in the shaders: no material holds a second, and the sea does nothing to any of them each frame
        // (the first version moved a clock on five hundred land materials a frame, which left five hundred wrappers a frame for the finalizer).
        var everyMaterial = waters.Concat(open.ShoreLandMaterials).ToArray();
        Check(everyMaterial.All(m => Mathf.IsEqualApprox(m.GetShaderParameter("shore_hold_s").AsSingle(), ShoreWash.FreeRunning)) && open.ShoreHeldAtS == null,
            "the wash moves with the engine's clock: no material holds a second");
        var camera = new Camera3D { Far = 100f, Position = new Vector3(0.2f, 0.1f, 0.4f) };
        holder.AddChild(camera);
        open.Camera = camera;
        for (var i = 0; i < 20; i++) open._Process(1.0 / 60.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (var i = 0; i < 400; i++) open._Process(1.0 / 60.0);
        sw.Stop();
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 400.0;
        GD.Print($"LOOK_INFO: the sea's frame (OpenSea._Process) allocates {perFrame:0} bytes and takes {sw.Elapsed.TotalMilliseconds / 400.0 * 1000.0:0.0} us on a headless synthetic island, the shore's {everyMaterial.Length} materials on");
        Check(perFrame < 512.0, $"the shore costs the sea's frame nothing: {perFrame:0} bytes allocated a frame (it was 48 KB and 515 finalizer wrappers a frame with a clock set on every land material)");
        open.HoldShoreAt(3.0f);
        Check(open.ShoreHeldAtS == 3.0f && everyMaterial.All(m => Mathf.IsEqualApprox(m.GetShaderParameter("shore_hold_s").AsSingle(), 3.0f)), "a capture can hold the wash at a chosen second, on every material");
        open.ReleaseShore();
        Check(open.ShoreHeldAtS == null && everyMaterial.All(m => Mathf.IsEqualApprox(m.GetShaderParameter("shore_hold_s").AsSingle(), ShoreWash.FreeRunning)), "released, it runs on the engine's time again");
        var materials = open.ShoreLandMaterials.Concat(waters).ToArray();
        holder.QueueFree();
        await Frames(2);
        Check(materials.All(m => !m.GetShaderParameter("shore_on").AsBool()), "when the sea leaves, the shared materials are left as a room without a sea expects them: the shore off");
    }

    /// <summary>The real island garage, when exported: the shore on its own terrain, vertex by vertex.</summary>
    private async Task CheckShoreOnTheIsland(StylePreset preset)
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no island garage (-- --landscape=DIR); the shore's real-room checks were skipped"); return; }
        MaterialLibrary.Configure(preset);
        var land = RoomData.Load(directory);
        if (land.Sea == null) { GD.Print("LOOK_INFO: the exported garage has no sea; the shore's real-room checks were skipped"); return; }
        var (holder, look) = NewDirector(preset, land, "ShoreIslandHolder");
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        await Frames(2);
        var open = look.OpenSea;
        Check(open != null && open.ShoreField != null && open.ShoreLandMaterials.Count >= 3, $"the island's sea wears the shore, with {open?.ShoreLandMaterials.Count} land materials on it");
        if (open == null || open.ShoreField == null) { holder.QueueFree(); return; }
        var field = open.ShoreField;
        var sea = land.Sea;
        // The island's own frame: the terrain has a material for each tile's bake colour, so the shore is on hundreds; the sea must still do nothing to them each frame.
        var camera = new Camera3D { Far = 100f, Position = new Vector3(0.3f, 0.1f, 0.2f) };
        holder.AddChild(camera);
        open.Camera = camera;
        for (var i = 0; i < 20; i++) open._Process(1.0 / 60.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) open._Process(1.0 / 60.0);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 200.0;
        Check(perFrame < 512.0, $"on the island, with the shore on {open.ShoreLandMaterials.Count} land materials, the sea's frame allocates {perFrame:0} bytes");
        // The terrain's own vertices (every ground mesh, in the world): which of them the shore would touch, at ten moments of a wash.
        var level = sea.LevelM;
        long inBand = 0, touched = 0, near = 0, farAndGated = 0, farTouched = 0;
        var widest = 0f;
        foreach (var mesh in built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.HasMeta(LandscapeLook.LandscapePaintedMeta) && !m.HasMeta(LookDirector.WaterMeta) && m.Mesh != null))
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array() : null;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = mesh.GlobalTransform * vertices[i];
                    var rel = world.Y - level;
                    if (rel < -Shore.DrawBackM - ShoreWash.BandBelowM - 0.001f || rel > Shore.RunUpM + Shore.WetAboveM + ShoreWash.BandAboveM + 0.001f) continue;
                    inBand++;
                    var xz = new Vector2(world.X, world.Z);
                    var ny = normals != null ? (mesh.GlobalTransform.Basis * normals[i]).Normalized().Y : 1f;
                    var coast = field.DistanceAt(xz);
                    var truth = RoomSea.Nearest(sea.Coast, xz).Distance;
                    var any = false;
                    for (var clock = 0f; clock < Shore.PeriodS * 2f; clock += Shore.PeriodS / 7f)
                    {
                        var (wet, film) = ShoreWash.OnLand(rel, xz, clock, coast, ShoreWash.SlopeOf(ny), Shore);
                        any |= wet > 1e-3f || film > 1e-4f;
                    }
                    if (any) { touched++; widest = MathF.Max(widest, truth); }
                    if (truth > Shore.ReachM + field.CellM + 0.02f) { farAndGated++; if (any) farTouched++; } else near++;
                }
            }
        GD.Print($"LOOK_INFO: the shore on the island: of {inBand} ground vertices within the beach's heights, {touched} are touched (damp or washed), the farthest {widest * 100f:0} cm from the coast; {farAndGated} lie farther than the reach ({Shore.ReachM:0.0} m) and none is touched");
        Check(inBand > 100 && touched > 20, $"the shore touches the island's beaches: {touched} of the {inBand} ground vertices at beach heights");
        Check(farTouched == 0 && widest <= Shore.ReachM + field.CellM + 0.02f, $"the damp band lies within the named width of the water's edge: no vertex farther than {widest * 100f:0} cm from the coast is touched ({Shore.ReachM:0.0} m is the reach), and {farAndGated} low-lying vertices inland are left exactly as they were");
        var materials = open.ShoreLandMaterials.ToArray();
        holder.QueueFree();
        await Frames(2);
        Check(materials.All(m => !m.GetShaderParameter("shore_on").AsBool()), "the island's land is left with the shore off when its sea leaves");
    }
}
