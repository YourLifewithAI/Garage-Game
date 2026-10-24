using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// Rooms written at test time under user://look_fixtures, each the test room with one thing changed, so the look can be
/// checked against the shapes captured rooms take (a window that is only an opening in a solid wall, no sun hint, three
/// windows, single-sided walls) without a captured room in the repository. Used by the headless look test and the GPU
/// probes of the capture harness. Nothing here uses the look's own types, so the same file runs against older commits.
/// </summary>
public static class LookFixtureRooms
{
    public const string Root = "user://look_fixtures";

    /// <summary>
    /// Write the test room with edits as a room named id, and return its directory. The edit sees the manifest as JSON; extra
    /// files (path relative to the room, bytes) are written and listed with their hashes.
    /// </summary>
    public static string Write(string id, Action<JsonObject> edit, params (string Path, byte[] Bytes)[] extraFiles)
    {
        var source = RoomWorld.DefaultRoom;
        var root = JsonNode.Parse(FileAccess.GetFileAsBytes(source + "/room.json"))!.AsObject();
        var directory = $"{Root}/{id}";
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(directory));
        foreach (var entry in root["files"]!.AsArray())
            Save($"{directory}/{entry!["path"]!.GetValue<string>()}", FileAccess.GetFileAsBytes($"{source}/{entry["path"]!.GetValue<string>()}"));
        edit(root);
        var files = root["files"]!.AsArray();
        foreach (var (path, bytes) in extraFiles)
        {
            Save($"{directory}/{path}", bytes);
            files.Add(new JsonObject { ["path"] = path, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["bytes"] = bytes.Length });
        }
        root["room_id"] = id;
        root["display_name"] = "Look fixture " + id;
        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
        Save($"{directory}/room.json", new UTF8Encoding(false).GetBytes(text));
        return directory;
    }

    private static void Save(string path, byte[] bytes)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(path[..path.LastIndexOf('/')]));
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        file.StoreBuffer(bytes);
    }

    public static JsonArray Hints(JsonObject room) => room["light_hints"]!.AsArray();

    public static void RemoveSunHint(JsonObject room)
    {
        var hints = Hints(room);
        foreach (var hint in hints.Where(h => h!["kind"]!.GetValue<string>() == "sun").ToArray()) hints.Remove(hint);
    }

    /// <summary>The test room's west wall in four parts around the window becomes one solid wall; the window stays listed as an opening only, as in a captured room.</summary>
    public static void SolidWestWall(JsonObject room)
    {
        var parts = room["shell"]!["parts"]!.AsArray();
        foreach (var part in parts.Where(p => p!["id"]!.GetValue<string>().StartsWith("shell:wall_west", StringComparison.Ordinal)).ToArray()) parts.Remove(part);
        parts.Add(JsonNode.Parse("""
            { "id": "shell:wall_west", "role": "wall",
              "geometry": { "kind": "polygon", "points_m": [[-2.0, 0, 1.5], [-2.0, 0, -1.5], [-2.0, 2.4, -1.5], [-2.0, 2.4, 1.5]], "thickness_m": 0.1 },
              "collides": true, "material_role": "painted_wall", "base_color": "#b8c7c8" }
            """));
    }

    /// <summary>The room with its sun hint taken away and its window only an opening in a solid wall: the shape of a captured room.</summary>
    public static string CapturedLike(string id = "captured_like") => Write(id, room => { RemoveSunHint(room); SolidWestWall(room); });

    /// <summary>The test room with a real window hole but no sun hint.</summary>
    public static string WindowWithoutSunHint(string id = "window_no_sun") => Write(id, RemoveSunHint);

    /// <summary>The test room with two more windows, in the north and south walls, each in a solid wall, of falling brightness: the third gets no shadow.</summary>
    public static string ThreeWindows(string id = "three_windows") => Write(id, room =>
    {
        var hints = Hints(room);
        foreach (var hint in hints.Where(h => h!["id"]!.GetValue<string>() == "window_west").ToArray()) hint!["relative_intensity"] = 0.9;
        hints.Add(JsonNode.Parse("""{ "id": "window_north", "kind": "window", "position_m": [0.8, 1.25, -1.55], "direction": [0.0, -0.3, 1.0], "color": "#d6e2f5", "relative_intensity": 0.7, "estimated": false }"""));
        hints.Add(JsonNode.Parse("""{ "id": "window_south", "kind": "window", "position_m": [-0.5, 1.25, 1.55], "direction": [0.0, -0.3, -1.0], "color": "#d6e2f5", "relative_intensity": 0.5, "estimated": false }"""));
    });

    /// <summary>
    /// A window-less room whose west wall is a single-sided mesh (one quad facing into the room), as photographed rooms'
    /// walls are, with the sun hint left in: the sun is outside and the wall must hold it back.
    /// </summary>
    public static string SingleSidedWestWall(string id = "single_sided")
    {
        var glb = WallGlb();
        return Write(id, room =>
        {
            var parts = room["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["id"]!.GetValue<string>().StartsWith("shell:wall_west", StringComparison.Ordinal)).ToArray()) parts.Remove(part);
            parts.Add(JsonNode.Parse("""
                { "id": "shell:wall_west", "role": "wall", "geometry": { "kind": "mesh", "mesh": "shell/wall_west.glb" },
                  "collides": true, "material_role": "painted_wall", "base_color": "#b8c7c8" }
                """));
            room["shell"]!["openings"] = new JsonArray();
            var hints = Hints(room);
            foreach (var hint in hints.Where(h => h!["kind"]!.GetValue<string>() == "window").ToArray()) hints.Remove(hint);
        }, ("shell/wall_west.glb", glb));
    }

    /// <summary>One quad on the plane x = -2, facing +X (into the room), with a small colour texture: the shape and material of a captured wall.</summary>
    public static byte[] WallGlb()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        Vector3[] corners = { new(-2f, 0f, 1.5f), new(-2f, 0f, -1.5f), new(-2f, 2.4f, -1.5f), new(-2f, 2.4f, 1.5f) };
        Vector2[] uv = { new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f) };
        foreach (var index in new[] { 0, 2, 1, 0, 3, 2 })
        {
            tool.SetNormal(Vector3.Right);
            tool.SetUV(uv[index]);
            tool.AddVertex(corners[index]);
        }
        var mesh = tool.Commit();
        var image = Image.CreateEmpty(32, 32, false, Image.Format.Rgb8);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                image.SetPixel(x, y, new Color(0.84f, 0.8f, 0.7f).Lerp(new Color(0.55f, 0.62f, 0.66f), ((x * 7 + y * 13) % 11) / 11f));
        var material = new StandardMaterial3D { ResourceName = "wall", AlbedoColor = new Color(1f, 1f, 1f), AlbedoTexture = ImageTexture.CreateFromImage(image) };
        mesh.SurfaceSetMaterial(0, material);
        var scene = new Node3D { Name = "wall" };
        scene.AddChild(new MeshInstance3D { Name = "quad", Mesh = mesh });
        var document = new GltfDocument();
        var state = new GltfState();
        document.AppendFromScene(scene, state);
        var bytes = document.GenerateBuffer(state);
        scene.Free();
        return bytes;
    }
}
