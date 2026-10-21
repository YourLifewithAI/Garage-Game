using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Fractals;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, L9: the look of a Fractal (docs/look/FRACTALS.md). The shaders take exactly what the look sets; the bodies stay inside the
/// kernel's box through their whole life, which is deterministic for the same ticks; a synthetic room whose manifest places Fractals
/// draws exactly one body and one glint for each still on the island, and a pickup through the host takes one away on NatureChanged
/// and starts the moment (it goes out, sparks rise and are gone); the glint keeps its pixels at the island's far plane and fades with
/// its haze; a thousand Fractals (the kernel's most) are drawn within budget and nothing is allocated in steady state; and on the 10x
/// garage, when the fixture names it, every Fractal of the island is drawn, within its frame budget, and the look suite's two Fractal
/// cameras look at one.
/// </summary>
public partial class LookPresetTest
{
    private const string FractalSaves = LookFixtureRooms.Root + "/fractal_saves";

    private async Task CheckFractals(StylePreset preset)
    {
        CheckFractalShaders();
        CheckFractalLife();
        await CheckFractalFixture(preset);
        await CheckFractalCapacity();
        await CheckRealFractals();
    }

    // ---------- the shaders and the bodies ----------

    /// <summary>Every uniform the look sets is declared and every declared uniform is set; the glint lights nothing and keeps its own haze.</summary>
    private void CheckFractalShaders()
    {
        foreach (var (path, names, materials) in new (string, string[], ShaderMaterial[])[]
                 {
                     (FractalLook.FormShaderPath, FractalLook.FormParameterNames, new[] { FractalLook.FormMaterial(FractalForm.Crystal), FractalLook.FormMaterial(FractalForm.Seed) }),
                     (FractalLook.GlintShaderPath, FractalLook.GlintParameterNames, new[] { FractalLook.GlintMaterial(star: false), FractalLook.GlintMaterial(star: true) }),
                 })
        {
            var shader = GD.Load<Shader>(path);
            // The depth buffer the star's soft edge reads is the engine's to give, as the water's is.
            var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).Where(n => n != "depth_texture").ToHashSet();
            var set = names.ToHashSet();
            Check(declared.Count > 5 && set.Except(declared).Count() == 0, $"every parameter FractalLook gives {path} is one of its uniforms: not declared " + string.Join(",", set.Except(declared)));
            Check(declared.Except(set).Count() == 0, $"every uniform of {path} is set by FractalLook: unset " + string.Join(",", declared.Except(set)));
            var missing = materials.SelectMany(m => names.Where(n => m.GetShaderParameter(n).VariantType == Variant.Type.Nil).Select(n => $"{m.ResourceName}.{n}")).ToArray();
            Check(missing.Length == 0, $"each of its materials has every one of them set: missing " + string.Join(",", missing));
        }
        var glint = Regex.Replace(GD.Load<Shader>(FractalLook.GlintShaderPath).Code, @"//[^\n]*", "");
        Check(Regex.IsMatch(glint, @"render_mode[^;]*\bblend_add\b") && Regex.IsMatch(glint, @"render_mode[^;]*\bunshaded\b") && Regex.IsMatch(glint, @"render_mode[^;]*\bfog_disabled\b")
              && glint.Contains("fog_density", StringComparison.Ordinal) && glint.Contains("min_pixels", StringComparison.Ordinal),
            "the glint is light added to what is behind it, never smaller than its pixels, and hidden by the look's own haze (the engine's fog would add its colour to the quad)");
        // The 15 October capture: in a render target the engine flips the projection's Y, so its [1][1] is negative; a glint sized by
        // it never grew and was a pixel at 3 m and nothing at 14. And a far glint must rise clear of the grass round its Fractal.
        Check(Regex.IsMatch(glint, @"abs\(\s*PROJECTION_MATRIX\[1\]\[1\]\s*\)") && !Regex.IsMatch(glint, @"[^(]PROJECTION_MATRIX\[1\]\[1\]\s*\*")
              && Regex.IsMatch(glint, @"VIEW_MATRIX\s*\*\s*vec4\(0\.0,\s*1\.0,\s*0\.0,\s*0\.0\)") && glint.Contains("grown - 1.0", StringComparison.Ordinal),
            "the glint's pixel rule takes the projection's size, never its sign (a render target flips it), and a far glint rises by half of what it grew, clear of the grass");
        Check(FractalMesh.Triangles(FractalForm.Crystal) == 144 && FractalMesh.Triangles(FractalForm.Seed) is > 100 and < 200 && FractalMesh.Triangles(FractalForm.Star) == 0,
            $"the bodies are under the fish's 200 triangles: the crystal {FractalMesh.Triangles(FractalForm.Crystal)} (the Koch tetrahedron's second step), the seed {FractalMesh.Triangles(FractalForm.Seed)}, the star none (it is light)");
        var forms = Enum.GetValues<FractalForm>();
        Check(forms.All(f => FractalLook.FormPin(new[] { FractalLook.FormArgument + f.ToString().ToLowerInvariant() }, null) == f)
              && FractalLook.FormPin(Array.Empty<string>(), "Star ") == FractalForm.Star && FractalLook.FormPin(new[] { FractalLook.FormArgument + "seed" }, "star") == FractalForm.Seed
              && FractalLook.FormPin(new[] { FractalLook.FormArgument + "cube" }, null) == null && FractalLook.FormPin(Array.Empty<string>(), null) == null,
            "a capture pins the form by --look-fractal-form= (first) or ENFRACTAL_LOOK_FRACTAL_FORM; anything else leaves the tuning's");
    }

    /// <summary>The slow life: inside the box at every moment, the turn and the bob as tuned, deterministic, and no two Fractals in step.</summary>
    private void CheckFractalLife()
    {
        var rest = new Vector3(0.3f, 0.1f, -0.2f);
        var box = CommandHost.FractalBox(new IslandFractal("fractal:life", rest, null, 1)).Grow(1e-5f);
        foreach (var form in Enum.GetValues<FractalForm>())
        {
            var outside = 0;
            float low = float.MaxValue, high = float.MinValue;
            foreach (var phase in new[] { 0f, 0.137f, 0.5f, 0.93f })
                for (var t = 0.0; t < 40.0; t += 0.025)
                {
                    var bounds = FractalLook.BodyBounds(form, rest, phase, t);
                    if (!box.Encloses(bounds)) outside++;
                    var y = FractalLook.BodyCentre(form, rest, phase, t).Y;
                    low = Math.Min(low, y);
                    high = Math.Max(high, y);
                }
            var bob = form == FractalForm.Star ? FractalLook.Tuning.StarBobM : FractalLook.Tuning.BobM;
            var (half, radius) = FractalMesh.Extents(form);
            Check(outside == 0 && Mathf.Abs((high - low) * 0.5f - bob) < 2e-4f,
                $"the {form} stays inside the kernel's 4 cm box through 40 s of its life ({outside} moments outside; half height {half * 100f:0.##} cm, radius {radius * 100f:0.##} cm), bobbing {(high - low) * 50f:0.##} cm each way");
        }
        var turned = FractalLook.Turn(0.3f, 5.0 + FractalLook.Tuning.TurnPeriodS);
        var bobbed = FractalLook.BodyCentre(FractalForm.Crystal, rest, 0.3f, 5.0 + FractalLook.Tuning.BobPeriodS);
        Check(Mathf.Abs(Mathf.AngleDifference(turned, FractalLook.Turn(0.3f, 5.0))) < 1e-3f && bobbed.DistanceTo(FractalLook.BodyCentre(FractalForm.Crystal, rest, 0.3f, 5.0)) < 1e-5f
              && FractalLook.BodyTransform(FractalForm.Seed, rest, 0.3f, 7.25).IsEqualApprox(FractalLook.BodyTransform(FractalForm.Seed, rest, 0.3f, 7.25)),
            $"it turns once in {FractalLook.Tuning.TurnPeriodS} s and bobs once in {FractalLook.Tuning.BobPeriodS} s, the same at the same moment");
        var ids = Enumerable.Range(0, 24).Select(i => $"fractal:{i:x6}").ToArray();
        var phases = ids.Select(FractalLook.PhaseOf).ToArray();
        var twinkles = Enumerable.Range(0, 600).Select(i => FractalLook.Twinkle(phases[0], i * 0.05)).ToArray();
        var together = Enumerable.Range(0, 600).Count(i => Mathf.Abs(FractalLook.Twinkle(phases[0], i * 0.05) - FractalLook.Twinkle(phases[1], i * 0.05)) < 0.02f);
        Check(phases.Distinct().Count() == ids.Length && phases.All(p => p is >= 0f and < 1f) && FractalLook.PhaseOf(ids[3]) == phases[3]
              && twinkles.Min() >= FractalLook.Tuning.TwinkleFloor - 1e-4f && twinkles.Max() > 0.95f && together < 300,
            $"each Fractal's phase comes from its id (24 ids, {phases.Distinct().Count()} phases); its glint twinkles between {twinkles.Min():0.##} and {twinkles.Max():0.##}, and two neighbours are in step only {together} of 600 moments");
    }

    // ---------- a synthetic island ----------

    /// <summary>The test room with Fractals in its manifest: one at the player's reach in front of the spawn, the rest round the room.</summary>
    private static string FractalRoom(string id, IEnumerable<(string Id, Vector3 At)> fractals) => LookFixtureRooms.Write(id, room =>
    {
        var items = new JsonArray();
        foreach (var (fid, at) in fractals) items.Add(new JsonObject { ["id"] = fid, ["position_m"] = new JsonArray(at.X, at.Y, at.Z), ["reach_band"] = 1 });
        room["extensions"] = new JsonObject { ["x_fractals"] = new JsonObject { ["typed"] = false, ["items"] = items } };
    });

    private sealed class MemoryVitals : IPlayerVitalsStore
    {
        private PlayerModesProfile _saved = PlayerModesProfile.Fresh.Select("challenge");
        public PlayerModesProfile Load() => _saved;
        public void Save(PlayerModesProfile profile) => _saved = profile;
    }

    /// <summary>A world as the kernel's tests build one: the room, the two avatars and a host whose save starts empty.</summary>
    private async Task<(Node3D World, CommandHost Host, SmallPlayerController Player)> FractalWorld(RoomData room, string name)
    {
        var saves = ProjectSettings.GlobalizePath($"{FractalSaves}/{name}");
        if (System.IO.Directory.Exists(saves)) System.IO.Directory.Delete(saves, true);
        var world = new Node3D { Name = "FractalWorld_" + name };
        AddChild(world);
        world.AddChild(RoomBuilder.Build(room));
        await Frames(2);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        world.AddChild(player);
        var companion = new CompanionAvatar { Name = "Companion", Position = room.SpawnFor("companion", room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        world.AddChild(companion);
        companion.BindPlayer(player);
        companion.Stay();
        var host = CommandHost.Create(world, room, player, companion, null, $"{FractalSaves}/{name}/inventions.json");
        host.LoadRules(RoomWorld.DefaultRulesId, RoomWorld.DefaultRulesVersion);
        host.ConfigureNature();
        host.ConfigurePlayerVitals(new MemoryVitals());
        await Frames(4);
        return (world, host, player);
    }

    /// <summary>
    /// One body and one glint for each Fractal still on the island; each body inside its box; a pickup through the host (the player
    /// placed in reach) takes it away on NatureChanged and starts the moment, which ends on time; the glint's far reach and haze.
    /// </summary>
    private async Task CheckFractalFixture(StylePreset preset)
    {
        var placed = new List<(string, Vector3)> { ("fractal:near01", new Vector3(0f, 0f, 0.5f)) };
        placed.AddRange(Enumerable.Range(0, 5).Select(i => ($"fractal:room{i:00}", new Vector3(-1.6f + i * 0.8f, 0f, -1.1f + (i % 2) * 0.4f))));
        var room = RoomData.Load(FractalRoom("fractal_room", placed));
        var (world, host, player) = await FractalWorld(room, "fixture");
        var (lookHolder, look) = NewDirector(preset, room, "FractalLookHolder");
        var fractals = FractalLook.Create(host, look);
        fractals.Running = false;
        world.AddChild(fractals);
        fractals.Advance(0.016f);
        var onIsland = host.Fractals.Select(f => f.Id).ToArray();
        var bodies = fractals.Bodies!;
        var glints = fractals.Glints!;
        var sparks = fractals.Sparks!;
        Check(host.AllFractals.Count == 6 && onIsland.Length == 6 && fractals.Count == 6 && fractals.Drawn.SequenceEqual(onIsland)
              && bodies.Multimesh.VisibleInstanceCount == 6 && glints.Multimesh.VisibleInstanceCount == 6 && sparks.Multimesh.VisibleInstanceCount == 0,
            $"a room whose manifest places six Fractals draws six bodies and six glints, in the host's order, and no sparks ({fractals.Summary})");
        Check(bodies.Multimesh.InstanceCount == host.AllFractals.Count && fractals.GetChildren().OfType<MultiMeshInstance3D>().Count() == 3
              && fractals.FindChildren("*", "Light3D", true, false).Count == fractals.Lights.Count && fractals.FindChildren("*", "CollisionObject3D", true, false).Count == 0
              && glints.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off
              && bodies.CastShadow == (fractals.Form == FractalForm.Star ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On)
              && bodies.Layers == 1u && glints.Layers == 1u,
            $"three MultiMeshes (bodies, glints, sparks) and the night's lights, no collider; the {fractals.Form} " + (fractals.Form == FractalForm.Star ? "is light and casts no shadow" : "casts a small shadow") + ", the glint none; both on the world layer every view draws");
        // The light at night (the founder, 15 October): a small pool round each Fractal's heart, no shadow, on when lamps would be.
        look.SetClock(2f, 172);
        fractals.Advance(FractalLook.Tuning.LightChooseS + 0.05f);
        var lights = fractals.Lights;
        var centres = Enumerable.Range(0, host.AllFractals.Count).Select(i => FractalLook.BodyCentre(fractals.Form, host.AllFractals[i].PositionM, FractalLook.PhaseOf(host.AllFractals[i].Id), fractals.LifeS)).ToArray();
        Check(lights.Count == 6 && fractals.Night > 0.99f
              && lights.All(l => l.Visible && Mathf.IsEqualApprox(l.LightEnergy, FractalLook.Tuning.LightEnergy) && Mathf.IsEqualApprox(l.OmniRange, FractalLook.Tuning.LightRangeM)
                                 && !l.ShadowEnabled && l.LightBakeMode == Light3D.BakeMode.Disabled)
              && lights.Select(l => centres.Count(c => c.DistanceTo(l.Position) < 1e-4f)).All(n => n == 1),
            $"at night (02:00) each of the six Fractals casts a small pool of light from its heart ({FractalLook.Tuning.LightEnergy} reaching {FractalLook.Tuning.LightRangeM} m), with no shadow (no slot of the shadow budget) and baked into nothing");
        look.SetClock(12f, 172);
        fractals.Advance(0.016f);
        Check(fractals.Night == 0f && lights.All(l => !l.Visible && l.LightEnergy == 0f), "by day (noon) the lights are out: the glint alone");
        look.SetClock(2f, 172);
        fractals.Advance(0.016f);
        var inside = true;
        var lifted = true;
        var asLived = true;
        for (var step = 0; step < 240; step++)
        {
            fractals.Advance(0.05f);
            for (var i = 0; i < fractals.Count; i++)
            {
                var fractal = host.AllFractals[i];
                var body = fractals.Instance(bodies, i).Transform;
                var (half, radius) = FractalMesh.Extents(fractals.Form);
                var extent = new Aabb(body.Origin - new Vector3(radius, half, radius), new Vector3(radius, half, radius) * 2f);
                inside &= CommandHost.FractalBox(fractal).Grow(1e-5f).Encloses(extent);
                // The star's glint is its heart, in the same place; any other body's stands above it.
                var glintAt = fractals.Instance(glints, i).Transform.Origin;
                lifted &= fractals.Form == FractalForm.Star ? glintAt.IsEqualApprox(body.Origin) : glintAt.Y > body.Origin.Y;
                // The star is a quad of light facing the eye: only its place follows the life (it bobs, it does not turn).
                asLived &= fractals.Form == FractalForm.Star
                    ? body.Origin.IsEqualApprox(FractalLook.BodyCentre(fractals.Form, fractal.PositionM, FractalLook.PhaseOf(fractal.Id), fractals.LifeS))
                    : body.IsEqualApprox(FractalLook.BodyTransform(fractals.Form, fractal.PositionM, FractalLook.PhaseOf(fractal.Id), fractals.LifeS));
            }
        }
        Check(inside && lifted && asLived, "through 12 s of drawn life every body is drawn as its life says, stays inside its Fractal's box, and its glint stands over it");
        var before = fractals.Instance(bodies, 0).Transform;
        fractals.Advance(0.5f);
        Check(!fractals.Instance(bodies, 0).Transform.IsEqualApprox(before), "the bodies move with the ticks (the slow life is drawn)");

        // The pickup, through the host as the HUD sends it, with the player in reach of the Fractal in front of the spawn.
        var restScale = fractals.Instance(bodies, 0).Transform.Basis.Column0.Length();
        player.TryTeleportTo(new Vector3(0f, 0f, 0.62f));
        await Frames(2);
        var picked = host.PlayerPickUp("fractal:near01");
        Check(picked["ok"]?.GetValue<bool>() == true && host.Fractals.Count() == 5, "the player picks up the Fractal in reach (item.pickup through the host): " + picked.ToJsonString());
        Check(fractals.Count == 6, "the look waits for the host's NatureChanged (read on its next tick), never the command itself");
        fractals.Advance(0.016f);
        var leavingScale = fractals.Instance(bodies, 0).Transform.Basis.Column0.Length();
        Check(fractals.Count == 5 && fractals.Leaving == 1 && fractals.SparkCount == FractalLook.Tuning.SparksPerPickup && sparks.Multimesh.VisibleInstanceCount == FractalLook.Tuning.SparksPerPickup
              && !fractals.Drawn.Contains("fractal:near01") && leavingScale < restScale,
            $"on NatureChanged the picked Fractal goes out (its body at {leavingScale / restScale:0.##} of its size) and {FractalLook.Tuning.SparksPerPickup} sparks rise from its spot");
        var spot = CommandHost.FractalBox(host.AllFractals.First(f => f.Id == "fractal:near01"));
        var sparksStart = Enumerable.Range(0, fractals.SparkCount).Select(i => fractals.Instance(sparks, i).Transform.Origin).ToArray();
        fractals.Advance(FractalLook.Tuning.PickupFadeS);
        var sparksLater = Enumerable.Range(0, fractals.SparkCount).Select(i => fractals.Instance(sparks, i).Transform.Origin).ToArray();
        Check(fractals.Leaving == 0 && bodies.Multimesh.VisibleInstanceCount == 5 && glints.Multimesh.VisibleInstanceCount == 5
              && sparksStart.All(s => s.DistanceTo(spot.GetCenter()) < 0.05f) && sparksLater.Length == sparksStart.Length && sparksLater.Zip(sparksStart).All(p => p.First.Y > p.Second.Y),
            $"after {FractalLook.Tuning.PickupFadeS} s it is gone (five bodies, five glints), while its sparks, which began at its spot, are still rising");
        fractals.Advance(FractalLook.Tuning.SparkLifeS * 1.3f);
        Check(fractals.SparkCount == 0 && sparks.Multimesh.VisibleInstanceCount == 0, "the sparks are gone within their life: the moment is over");
        Check(fractals.Lights.Count(l => l.Visible) == 5 && fractals.Lights.Where(l => l.Visible).All(l => l.Position.DistanceTo(spot.GetCenter()) > 0.05f),
            "and its light has gone out with it: five pools of light at night, none where it rested");

        // The glint at the island's far plane: the shader's rule (wider with distance, never under its pixels) keeps it on screen.
        var far = ViewDistances.MaxFarM;
        const float fov = 75f;
        var pixelM = 2f * far * Mathf.Tan(Mathf.DegToRad(fov) * 0.5f) / 1080f;
        var drawnM = Mathf.Max(FractalLook.Tuning.GlintSizeM, pixelM * FractalLook.Tuning.GlintMinPixels);
        var glintMaterial = (ShaderMaterial)glints.MaterialOverride;
        Check(drawnM / pixelM >= FractalLook.Tuning.GlintMinPixels - 1e-3f && glintMaterial.GetShaderParameter("min_pixels").AsSingle() >= 6f && glints.VisibilityRangeEnd == 0f
              && glints.CustomAabb.Size.Length() > 8f && ((ShaderMaterial)bodies.MaterialOverride).GetShaderParameter("far_m").AsSingle() < far,
            $"at the island's far plane ({far} m, a 1080p view of {fov} degrees) a glint is drawn {drawnM:0.##} m wide, {FractalLook.Tuning.GlintMinPixels} pixels: never culled, never under its pixels, while its body fades at {FractalLook.Tuning.FormFarM} m");
        // The haze: the glint mirrors the look's depth fog, so at the far plane it keeps what the land keeps.
        look.Environment.FogEnabled = true;
        look.Environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        look.Environment.FogDepthBegin = 40f;
        look.Environment.FogDepthEnd = 250f;
        look.Environment.FogDensity = 0.6f;
        look.Environment.FogDepthCurve = 1f;
        fractals.Advance(0.016f);
        var hazed = new DepthFog(true, glintMaterial.GetShaderParameter("fog_begin_m").AsSingle(), glintMaterial.GetShaderParameter("fog_end_m").AsSingle(),
            glintMaterial.GetShaderParameter("fog_density").AsSingle(), glintMaterial.GetShaderParameter("fog_curve").AsSingle());
        Check(glintMaterial.GetShaderParameter("fog_on").AsSingle() == 1f && Mathf.IsEqualApprox(ViewDistances.FogAt(hazed, far), 0.6f) && ViewDistances.FogAt(hazed, 20f) == 0f,
            $"the glint takes the look's haze (at {far} m it keeps {1f - ViewDistances.FogAt(hazed, far):0.##} of its light, as the land keeps of its colour)");

        // Every form draws the same Fractals; only the star, which is light, casts no shadow.
        foreach (var form in Enum.GetValues<FractalForm>())
        {
            fractals.SetForm(form);
            fractals.Advance(0.016f);
            Check(fractals.Count == 5 && fractals.Bodies!.Multimesh.VisibleInstanceCount == 5
                  && fractals.Bodies.CastShadow == (form == FractalForm.Star ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On),
                $"drawn as the {form}, the same five remain" + (form == FractalForm.Star ? ", casting no shadow" : ""));
        }
        lookHolder.QueueFree();
        world.QueueFree();
        await Frames(3);
    }

    /// <summary>A thousand Fractals (the kernel's most): every one drawn, within budget, nothing allocated as they live; one more and the host loads none.</summary>
    private async Task CheckFractalCapacity()
    {
        var thousand = Enumerable.Range(0, CommandHost.MaxFractals).Select(i => ($"fractal:m{i:0000}", new Vector3(-1.9f + (i % 40) * 0.095f, 0f, -1.4f + (i / 40) * 0.11f))).ToList();
        var room = RoomData.Load(FractalRoom("fractal_thousand", thousand));
        var (world, host, _) = await FractalWorld(room, "thousand");
        var fractals = FractalLook.Create(host, null);
        fractals.Running = false;
        world.AddChild(fractals);
        for (var i = 0; i < 30; i++) fractals.Advance(1f / 60f);
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        const int ticks = 120;
        for (var i = 0; i < ticks; i++) fractals.Advance(1f / 60f);
        var perTick = clock.Elapsed.TotalMilliseconds / ticks;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GD.Print($"LOOK_INFO: the Fractal look with {CommandHost.MaxFractals} Fractals: {perTick * 1000.0:0} us a tick headless, {allocated} bytes allocated over {ticks} ticks");
        Check(fractals.Count == CommandHost.MaxFractals && fractals.Bodies!.Multimesh.InstanceCount == CommandHost.MaxFractals && fractals.Bodies.Multimesh.VisibleInstanceCount == CommandHost.MaxFractals
              && fractals.Lights.Count == FractalLook.Tuning.MaxLights,
            $"the kernel's most, {CommandHost.MaxFractals} Fractals, are every one drawn and never more, and at most {FractalLook.Tuning.MaxLights} of them are lit at night (those nearest the player)");
        Check(allocated == 0 && perTick < 0.6, $"a thousand Fractals live within budget: {perTick * 1000.0:0} us a tick, {allocated} bytes allocated in steady state");
        world.QueueFree();
        await Frames(3);
        thousand.Add(("fractal:onemore", new Vector3(1.9f, 0f, 1.4f)));
        var over = RoomData.Load(FractalRoom("fractal_too_many", thousand));
        var (overWorld, overHost, _) = await FractalWorld(over, "too_many");
        var none = FractalLook.Create(overHost, null);
        overWorld.AddChild(none);
        Check(overHost.AllFractals.Count == 0 && overHost.FractalsNotice.Length > 0 && none.Bodies == null && none.Count == 0 && none.Summary.Contains(overHost.FractalsNotice, StringComparison.Ordinal),
            "a manifest placing one more than the kernel's most has none (the kernel refuses its block), and the look draws none, saying why: " + none.Summary);
        overWorld.QueueFree();
        await Frames(3);
    }

    /// <summary>The 10x garage, when the fixture names it: every Fractal of the island drawn, each in its box, and the cameras standing by one.</summary>
    private async Task CheckRealFractals()
    {
        var directory = FindLandscapeFixture();
        var land = directory != null ? RoomData.Load(directory) : null;
        if (land == null) { GD.Print("LOOK_INFO: no garage landscape: the island's Fractals are looked at with -- --landscape=DIR"); return; }
        var holder = new Node3D { Name = "RealFractalsHolder" };
        AddChild(holder);
        var host = CommandHost.Create(holder, land, null, null, null, $"{FractalSaves}/real/inventions.json");
        await Frames(2);
        if (host.AllFractals.Count == 0)
        {
            GD.Print($"LOOK_INFO: {land.RoomId} places no Fractals (the generator places them at 10x only); the 10x garage's are looked at with -- --landscape=<the 10x fixture>");
            holder.QueueFree();
            await Frames(1);
            return;
        }
        var fractals = FractalLook.Create(host, null);
        fractals.Running = false;
        holder.AddChild(fractals);
        fractals.Advance(0.016f);
        var inBox = Enumerable.Range(0, fractals.Count).All(i => CommandHost.FractalBox(host.AllFractals[i]).Grow(1e-5f).HasPoint(fractals.Instance(fractals.Bodies!, i).Transform.Origin));
        Check(host.FractalsNotice.Length == 0 && host.AllFractals.Count is >= 20 and <= 40 && fractals.Count == host.AllFractals.Count && inBox
              && fractals.Lights.Count == Math.Min(host.AllFractals.Count, FractalLook.Tuning.MaxLights),
            $"the 10x garage's {host.AllFractals.Count} Fractals are every one drawn, each in its own box ({fractals.Summary})");
        // Its cost a frame on the island (the CPU's; the GPU's is in the captures): the life, the glints and the buffers written.
        for (var i = 0; i < 60; i++) fractals.Advance(1f / 60f);
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        const int ticks = 600;
        for (var i = 0; i < ticks; i++) fractals.Advance(1f / 60f);
        var perTick = clock.Elapsed.TotalMilliseconds * 1000.0 / ticks;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GD.Print($"LOOK_INFO: the Fractal look on the 10x garage ({fractals.Count} Fractals): {perTick:0.0} us a frame headless, {allocated} bytes allocated over {ticks} frames");
        Check(perTick < 100.0 && allocated == 0, $"on the 10x garage it costs {perTick:0.0} us a frame on the CPU (the plan's target: under 0.1 ms) and allocates nothing in steady state ({allocated} bytes)");
        var cameras = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath("res://") + "../tools/look/island_cameras.json"));
        Vector3 Point(System.Text.Json.JsonElement e, string key) => new(e.GetProperty(key)[0].GetSingle(), e.GetProperty(key)[1].GetSingle(), e.GetProperty(key)[2].GetSingle());
        var fixedCameras = cameras.RootElement.GetProperty("cameras").EnumerateArray().Where(c => c.TryGetProperty("id", out var id) && id.GetString()!.StartsWith("fractal_", StringComparison.Ordinal)).ToArray();
        var looked = fixedCameras.Select(c => host.AllFractals.FirstOrDefault(f => CommandHost.FractalBox(f).Grow(0.01f).HasPoint(Point(c, "look_at_m")))).ToArray();
        Check(fixedCameras.Length == 2 && looked.All(f => f != null) && looked[0] == looked[1],
            $"the look suite's fractal_near and fractal_far cameras look at one Fractal of the island ({looked.FirstOrDefault()?.Id})");
        cameras.Dispose();
        holder.QueueFree();
        await Frames(2);
    }
}
