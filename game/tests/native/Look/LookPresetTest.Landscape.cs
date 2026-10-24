using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, Lane L: the landscape in the game. The harness's twenty roles each have their own marks; a landscape's baked vertex
/// colours stay inside the painterly treatment (the room look discarded them, so every blended ground role showed the
/// envelope's pale cream); a captured room's materials never take this path; open land is lit by a real sky and bakes no
/// closed interior; and, when the garage landscape has been exported (tools/test-room.ps1 does it first), the real room's
/// every surface wears a landscape material whose baked colour is the colour the generator meant.
/// </summary>
public partial class LookPresetTest
{
    private async Task CheckLandscape(StylePreset preset, RoomData testRoom)
    {
        CheckLandscapeRoles();
        CheckLandscapeShader(preset);
        await CheckLandscapeDressing(preset);
        await CheckOpenLand(preset, testRoom);
        await CheckRealLandscape(preset);
    }

    /// <summary>Every harness role has its own marks, the enum matches the shader's constants, and the shader keeps the vertex colour.</summary>
    private void CheckLandscapeRoles()
    {
        // The harness's own vocabulary is the source of truth: its palette table.
        var harness = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../pipeline/landscape/harness/common.py"));
        if (!System.IO.File.Exists(harness)) { GD.Print("LOOK_INFO: harness palette not found beside the project; the role list is not compared with it"); return; }
        var text = System.IO.File.ReadAllText(harness);
        var palette = text[text.IndexOf("PALETTE = {", StringComparison.Ordinal)..];
        palette = palette[..palette.IndexOf('}')];
        var roles = Regex.Matches(palette, @"'(\w+)':\s*\(").Select(m => m.Groups[1].Value).ToHashSet();
        Check(roles.Count == 20, $"the harness has twenty roles ({roles.Count})");
        Check(roles.SetEquals(LandscapeLook.Roles.Keys), "every harness role has its own landscape marks, and no other role does: " + string.Join(",", roles.Except(LandscapeLook.Roles.Keys).Concat(LandscapeLook.Roles.Keys.Except(roles))));
        var kinds = LandscapeLook.Roles.Values.Select(r => r.Kind).ToArray();
        Check(kinds.All(k => k != LandKind.None) && kinds.Distinct().Count() == kinds.Length, "each role has a mark of its own: meadow, soil, path, gravel, scree, rock, cliff, moss, snow, two waters, foliage, bark, timber, wood, masonry, roof, cloth, metal, stone");
        Check(LandscapeLook.Roles.Values.All(r => r.ScaleM > 0f && r.Scale2M > 0f && r.Variation > 0f && r.Roughness is >= 0f and <= 1f && r.Axis.Length() > 0.5f), "every role's numbers are sensible (sizes, variation, roughness, a stroke axis)");
        // The shader's K_ constants are the enum's values.
        var shader = GD.Load<Shader>(LandscapeLook.ShaderPath);
        var constants = Regex.Matches(shader.Code, @"const\s+int\s+K_(\w+)\s*=\s*(\d+)\s*;").ToDictionary(m => m.Groups[1].Value.Replace("_", "").ToLowerInvariant(), m => int.Parse(m.Groups[2].Value));
        var mismatched = Enum.GetValues<LandKind>().Where(k => !constants.TryGetValue(k.ToString().ToLowerInvariant(), out var value) || value != (int)k).ToArray();
        Check(mismatched.Length == 0 && constants.Count == Enum.GetValues<LandKind>().Length, "the shader's K_ constants are the LandKind enum's values: " + string.Join(",", mismatched));
        // The baked colour is the colour: the vertex colour reaches the fragment shader and multiplies the glTF factor.
        var code = Regex.Replace(Regex.Replace(shader.Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Check(Regex.IsMatch(code, @"baked_color\s*=\s*COLOR\.rgb\s*;") && Regex.IsMatch(code, @"color\s*=\s*base_color\s*\*\s*baked_color[^;]*;"),
            "the land shader keeps COLOR_0: the glTF factor times the baked vertex colour is the colour the marks start from");
    }

    /// <summary>Review finding style: every parameter is a uniform the shader declares, every uniform is set, the code reads them all.</summary>
    private void CheckLandscapeShader(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        foreach (var role in LandscapeLook.Roles.Keys) MaterialLibrary.ForLandscape(role, new Color("c7b8ad"), new Color("66a040"));
        var shader = GD.Load<Shader>(LandscapeLook.ShaderPath);
        var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        var set = MaterialLibrary.LandscapeParameterNames.ToHashSet();
        Check(declared.Count >= 25, $"the land shader lists its uniforms ({declared.Count})");
        var unknown = set.Except(declared).ToArray();
        Check(unknown.Length == 0, "every parameter a landscape material is given is a uniform of the land shader: not declared " + string.Join(",", unknown));
        var unset = declared.Except(set).ToArray();
        Check(unset.Length == 0, "every uniform of the land shader is set (none left at the shader default): unset " + string.Join(",", unset));
        var source = Regex.Replace(Regex.Replace(shader.Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        var declarations = Regex.Matches(source, @"^\s*(?:(?:instance|global)\s+)?uniform\s+\w+\s+(\w+)[^;]*;", RegexOptions.Multiline);
        var body = Regex.Replace(source, @"^\s*(?:(?:instance|global)\s+)?uniform\s+[^;]*;", "", RegexOptions.Multiline);
        var ignored = declarations.Select(m => m.Groups[1].Value).Where(n => !Regex.IsMatch(body, $@"\b{n}\b")).ToArray();
        // Besides the material's own: three per-instance values (the paint seed, the intro's mark, a plant's season habit), the intro's five globals (Look/Intro) and the land's season globals (LandSeason).
        string[] Scoped(string scope) => Regex.Matches(source, $@"^\s*{scope}\s+uniform\s+\w+\s+(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Order().ToArray();
        var instances = Scoped("instance");
        var globals = Scoped("global");
        Check(declarations.Count == declared.Count + instances.Length + globals.Length && instances.SequenceEqual(new[] { "intro_part", "paint_seed", "season_habit" })
            && globals.SequenceEqual(new[] { "intro_area", "intro_colour", "intro_grow", "intro_rise", "intro_start" }.Concat(LandSeason.Globals.Select(g => g.Name)).Concat(GrassLook.Globals.Select(g => g.Name)).Order()) && ignored.Length == 0,
            $"the land shader reads every uniform it declares (and its per-instance seed and intro mark, and the intro's globals: {string.Join(",", instances.Concat(globals))}): ignored " + string.Join(",", ignored));
        var lightFunction = body[body.IndexOf("void light()", StringComparison.Ordinal)..];
        Check(!Regex.IsMatch(lightFunction, @"1\.0\s*-\s*ATTENUATION") && Regex.IsMatch(lightFunction, @"band\s*=[^;]*\*\s*light_warmth\s*;"),
            "the land shader lights like the room's: no light added in a cast shadow, a warm terminator only from warm light");
        // The numbers come from the role table, and a role that is not the harness's is refused rather than painted by a guess.
        var meadow = (ShaderMaterial)MaterialLibrary.ForLandscape("meadow", new Color("c7b8ad"), new Color("66a040"));
        Check(meadow.GetShaderParameter("kind").AsInt32() == (int)LandKind.Meadow && Mathf.IsEqualApprox(meadow.GetShaderParameter("scale_m").AsSingle(), LandscapeLook.Roles["meadow"].ScaleM)
            && meadow.GetShaderParameter("base_color").AsColor().IsEqualApprox(new Color("c7b8ad")) && meadow.GetMeta("material_role").AsString() == "meadow",
            "a landscape material carries its role's mark, its sizes and the glTF factor it multiplies the vertex colour by");
        Check(ReferenceEquals(meadow, MaterialLibrary.ForLandscape("meadow", new Color("c7b8ad"), new Color("66a040"))) && !ReferenceEquals(meadow, MaterialLibrary.ForLandscape("soil", new Color("c7b8ad"), new Color("66a040"))),
            "landscape materials are cached per role, factor and bake colour");
        CheckThrows(() => MaterialLibrary.ForLandscape("lava", new Color("ff4400"), new Color("ff4400")), "lava", "a role the harness does not have is refused, naming it");
        MaterialLibrary.Configure(preset);
    }

    private static MeshInstance3D LandscapeMesh(string role, Color factorLinear, Color vertexLinear, Godot.Collections.Dictionary? extras = null)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        Vector3[] corners = { new(0f, 0f, 0f), new(0f, 0f, 1f), new(1f, 0f, 1f), new(1f, 0f, 0f) };
        foreach (var index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            tool.SetNormal(Vector3.Up);
            tool.SetColor(vertexLinear);
            tool.AddVertex(corners[index]);
        }
        var mesh = tool.Commit();
        var material = new StandardMaterial3D { ResourceName = role, AlbedoColor = factorLinear.LinearToSrgb() };
        material.SetMeta("extras", extras ?? new Godot.Collections.Dictionary { ["role"] = role, ["colors_baked"] = true, ["blend_roles"] = new Godot.Collections.Array(), ["role_base_color"] = new Godot.Collections.Array() });
        mesh.SurfaceSetMaterial(0, material);
        return new MeshInstance3D { Name = "Mesh", Mesh = mesh };
    }

    private static Node3D PartOwner(string id, bool shell)
    {
        var owner = new Node3D { Name = id.Replace(':', '_') };
        owner.SetMeta("entity_id", id);
        if (shell)
        {
            owner.SetMeta("surface_role", "ground");
            owner.SetMeta("material_role", "grass");
        }
        else owner.SetMeta("material_roles", new Godot.Collections.Dictionary { ["meadow"] = "grass" });
        return owner;
    }

    /// <summary>The cream bug: a blended role's glTF factor is the envelope, and its vertex colour is what makes it green.</summary>
    private async Task CheckLandscapeDressing(StylePreset preset)
    {
        MaterialLibrary.Configure(preset);
        var room = RoomData.Load(RoomWorld.DefaultRoom);
        var (holder, look) = NewDirector(preset, room, "LandscapeHolder");
        var root = new Node3D { Name = "LandscapeRoot" };
        holder.AddChild(root);
        // The exporter's meadow, as Godot imports it: the envelope of its blends (cream) as the material's colour and the
        // tinted blend over it (green) as the vertex colour.
        var envelope = new Color(0.57f, 0.49f, 0.43f);
        var green = new Color(0.31f, 0.73f, 0.14f);
        var terrain = PartOwner("shell:terrain_meadow", shell: true);
        var terrainMesh = LandscapeMesh("meadow", envelope, green);
        terrain.AddChild(terrainMesh);
        var cottage = PartOwner("obj:scatter_0001", shell: false);
        var cottageMesh = LandscapeMesh("meadow", envelope, green);
        cottage.AddChild(cottageMesh);
        // A captured room's material named like a role, without the exporter's marker, must keep its old treatment.
        var captured = PartOwner("shell:wall_captured", shell: true);
        var capturedMesh = LandscapeMesh("wood", new Color(0.5f, 0.4f, 0.3f), new Color(1f, 1f, 1f), new Godot.Collections.Dictionary { ["role"] = "wood" });
        captured.AddChild(capturedMesh);
        var unbaked = PartOwner("shell:wall_unbaked", shell: true);
        var unbakedMesh = LandscapeMesh("wood", new Color(0.5f, 0.4f, 0.3f), new Color(1f, 1f, 1f), new Godot.Collections.Dictionary { ["role"] = "wood", ["colors_baked"] = false });
        unbaked.AddChild(unbakedMesh);
        root.AddChild(terrain);
        root.AddChild(cottage);
        root.AddChild(captured);
        root.AddChild(unbaked);
        look.Dress(root);
        await Frames(2);

        var painted = terrainMesh.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
        Check(painted != null && painted.Shader.ResourcePath == LandscapeLook.ShaderPath && painted.GetMeta("material_role").AsString() == "meadow" && painted.HasMeta("landscape"),
            "a landscape's meadow surface becomes the land shader's meadow, named after its harness role (not the shell part's contract role, grass)");
        Check(painted != null && painted.GetShaderParameter("base_color").AsColor().IsEqualApprox(envelope.LinearToSrgb()) && terrainMesh.HasMeta(LandscapeLook.LandscapePaintedMeta),
            "it keeps the glTF factor, which the shader multiplies by the vertex colour, and the mesh is marked");
        var bake = painted != null ? MaterialLibrary.BakeAlbedo(painted) : null;
        var bakeLinear = bake?.SrgbToLinear();
        Check(bakeLinear is { } b && Mathf.Abs(b.R - envelope.R * green.R) < 0.01f && Mathf.Abs(b.G - envelope.G * green.G) < 0.01f && Mathf.Abs(b.B - envelope.B * green.B) < 0.01f,
            "the colour VoxelGI bakes is the factor times the baked vertex colour");
        Check(bakeLinear is { } c && c.G > c.R + 0.1f && c.G > c.B + 0.2f, $"and it is meadow green, not the envelope's cream (linear {bakeLinear})");
        Check(painted != null && terrainMesh.GetInstanceShaderParameter("paint_seed").VariantType == Variant.Type.Nil,
            "the land's shell parts take no paint seed, so a mark runs on across the parts that meet");
        Check(cottageMesh.GetInstanceShaderParameter("paint_seed").VariantType != Variant.Type.Nil, "a prop (an object) gets its own seed, so two cottages are painted differently");
        Check(capturedMesh.GetSurfaceOverrideMaterial(0) is ShaderMaterial { } roomWood && roomWood.Shader.ResourcePath == MaterialLibrary.ShaderPath && capturedMesh.HasMeta(LookDirector.CapturedPaintedMeta) && !capturedMesh.HasMeta(LandscapeLook.LandscapePaintedMeta),
            "a captured room's material named like a harness role, without colors_baked, still takes the room's own treatment");
        Check(unbakedMesh.GetSurfaceOverrideMaterial(0) is ShaderMaterial { } unbakedWood && unbakedWood.Shader.ResourcePath == MaterialLibrary.ShaderPath,
            "and so does one whose extras say colors_baked is false");
        // During a GI bake a landscape surface is voxelized as its baked colour, never the cream of the envelope; afterwards the paint is back.
        Material? during = null;
        LookDirector.WithBakeStandIns(new GeometryInstance3D[] { terrainMesh }, () => during = terrainMesh.GetSurfaceOverrideMaterial(0));
        Check(during is StandardMaterial3D { } standIn && standIn.AlbedoColor.IsEqualApprox(bake!.Value) && during.HasMeta(LookDirector.BakeStandInMeta) && terrainMesh.GetSurfaceOverrideMaterial(0) == painted,
            "during a GI bake the land is voxelized as its baked colour, and the painterly material comes back after");
        var threw = false;
        try { LookDirector.WithBakeStandIns(new GeometryInstance3D[] { terrainMesh }, () => throw new InvalidOperationException("bake failed")); } catch (InvalidOperationException) { threw = true; }
        Check(threw && terrainMesh.GetSurfaceOverrideMaterial(0) == painted, "a failed bake still restores the landscape material");
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>Open land: no wall, no ceiling. The sky lights it; there is no interior to bake.</summary>
    private async Task CheckOpenLand(StylePreset preset, RoomData testRoom)
    {
        Check(!LandscapeLook.IsOpenLand(testRoom), "the test room, with its walls and ceiling, is not open land");
        var directory = LookFixtureRooms.Write("open_land", room =>
        {
            var parts = room["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["role"]!.GetValue<string>() != "floor").ToArray()) parts.Remove(part);
            parts[0]!["role"] = "ground";
            room["shell"]!["openings"] = new JsonArray();
            LookFixtureRooms.RemoveSunHint(room);
            var hints = LookFixtureRooms.Hints(room);
            foreach (var hint in hints.ToArray()) hints.Remove(hint);
        });
        var land = RoomData.Load(directory);
        Check(LandscapeLook.IsOpenLand(land) && land.LightHints.Count == 0, "a shell of ground alone, with no window, lamp or sun hint, is open land");
        var (holder, look) = NewDirector(preset, land, "OpenLandHolder");
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        await Frames(3);
        Check(look.OpenLand && look.SunScale > 0.99f && look.Key.Visible, "on open land the sun shines at full strength with no opening and no sun hint: the sky lights the land");
        look.SetClock(12f, 172);
        var indoors = look.Moment.AmbientEnergy * preset.Tuning.Gi.EnvironmentAmbientScale;
        Check(Mathf.IsEqualApprox(look.Environment.AmbientLightEnergy, look.Moment.AmbientEnergy * OpenLandLight.AmbientScale) && look.Environment.AmbientLightEnergy > 3f * indoors,
            $"the sky's own light fills the shade on open land ({look.Environment.AmbientLightEnergy:0.###} against {indoors:0.###} indoors)");
        Check(Mathf.IsEqualApprox(look.Key.LightEnergy, look.Moment.KeyEnergy * OpenLandLight.SunStrength) && OpenLandLight.SunStrength < 0.6f
            && look.Key.DirectionalShadowMaxDistance >= OpenLandLight.ShadowDistanceM - 0.01f,
            $"the open land's sun is a fraction of the room look's ({look.Key.LightEnergy:0.##} of {look.Moment.KeyEnergy:0.##}: the whole ground stands in it, not one patch of floor) and its shadows reach the backdrop");
        Check(look.Gi == null && look.Dressed && look.GiNote.StartsWith("open land", StringComparison.Ordinal), "no closed interior is baked on open land, and the dressed ground still counts: " + look.GiNote);
        Check(!look.Warnings.Any(w => w.Contains("no room geometry", StringComparison.Ordinal)), "and the look does not warn that no shell was dressed");
        holder.QueueFree();
        // A room with walls keeps the indoor rule.
        var (indoorHolder, indoor) = NewDirector(preset, testRoom, "IndoorHolder");
        Check(Mathf.IsEqualApprox(indoor.Key.LightEnergy, indoor.Moment.KeyEnergy * indoor.SunScale), "and the indoor sun keeps its full strength");
        Check(!indoor.OpenLand && Mathf.IsEqualApprox(indoor.Environment.AmbientLightEnergy, indoor.Moment.AmbientEnergy * preset.Tuning.Gi.EnvironmentAmbientScale), "a room with walls keeps the indoor rule: the window is the sun's only way in, the ambient stays low");
        indoorHolder.QueueFree();
        await Frames(1);
    }

    /// <summary>The real garage landscape, when it has been exported: every surface wears a landscape material and the ground is green, not cream.</summary>
    private async Task CheckRealLandscape(StylePreset preset)
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no garage landscape (-- --landscape=DIR; tools/test-room.ps1 passes the shared fixture cache's); the real-room checks were skipped"); return; }
        MaterialLibrary.Configure(preset);
        var land = RoomData.Load(directory);
        var (holder, look) = NewDirector(preset, land, "RealLandscapeHolder");
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        await Frames(3);
        var meshes = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
        var surfaces = meshes.Sum(m => m.Mesh?.GetSurfaceCount() ?? 0);
        var landscape = meshes.Sum(m => Enumerable.Range(0, m.Mesh?.GetSurfaceCount() ?? 0).Count(s => m.GetSurfaceOverrideMaterial(s) is ShaderMaterial { } material && material.HasMeta("landscape")));
        Check(look.OpenLand && surfaces > 30 && landscape == surfaces, $"every surface of the garage landscape ({surfaces}) wears a landscape material ({landscape})");
        var roles = meshes.SelectMany(m => Enumerable.Range(0, m.Mesh?.GetSurfaceCount() ?? 0).Select(s => (m.GetSurfaceOverrideMaterial(s) as ShaderMaterial)?.GetMeta("material_role").AsString() ?? "")).ToHashSet();
        Check(roles.Count >= 15 && roles.All(LandscapeLook.Roles.ContainsKey), $"the garage uses {roles.Count} of the harness's roles, all with marks: {string.Join(",", roles.OrderBy(r => r))}");
        MeshInstance3D MeshOf(string id) => meshes.First(m => EntityOf(m) == id);
        var meadow = MaterialLibrary.BakeAlbedo(MeshOf("shell:terrain_meadow").GetSurfaceOverrideMaterial(0))!.Value.SrgbToLinear();
        Check(meadow.G > meadow.R * 1.5f && meadow.G > meadow.B * 2f, $"the garage's meadow bakes green, not cream (linear {meadow})");
        var factor = MeshOf("shell:terrain_meadow").GetSurfaceOverrideMaterial(0) is ShaderMaterial { } meadowMaterial
            ? meadowMaterial.GetShaderParameter("base_color").AsColor().SrgbToLinear() : default;
        Check(factor.R > factor.G && Mathf.Abs(factor.R - 0.57f) < 0.02f, $"while the glTF factor under it is the cream envelope (linear {factor}): the colour was always in the vertex colour");
        CheckIslandCameras(land, built);
        holder.QueueFree();
        await Frames(1);
    }

    private static string EntityOf(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
            if (current.HasMeta("entity_id")) return current.GetMeta("entity_id").AsString();
        return "";
    }

    /// <summary>
    /// "-- --landscape=DIR" (the runners pass the shared fixture cache's garage, tools/landscape-fixture.ps1), else a garage this
    /// worktree exported under its own .cache/landscape-fixture before the cache was shared.
    /// </summary>
    private static string? FindLandscapeFixture()
    {
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--landscape=", StringComparison.Ordinal)) is { } given)
        {
            var path = given["--landscape=".Length..].Replace('\\', '/');
            return System.IO.File.Exists(System.IO.Path.Combine(path, "room.json")) ? path : null;
        }
        var cache = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../.cache/landscape-fixture"));
        if (!System.IO.Directory.Exists(cache)) return null;
        return System.IO.Directory.GetDirectories(cache).Select(d => System.IO.Path.Combine(d, "landscape_garage_nominal"))
            .FirstOrDefault(d => System.IO.File.Exists(System.IO.Path.Combine(d, "room.json")))?.Replace('\\', '/');
    }
}
