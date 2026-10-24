using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look;

namespace GarageGame.Native.Room;

/// <summary>
/// Builds scene nodes from RoomData. The scene is derived: nothing here is authoritative state.
/// Shell polygons become extruded slabs; objects become StaticBody3D nodes for now (the Play
/// track decides which become rigid bodies). Every node carries its entity id, roles and
/// affordances as metadata so look, play and AI systems can find what they need.
/// </summary>
public static class RoomBuilder
{
    public const uint WorldLayer = 1;
    /// <summary>
    /// Physics layer 5, "hidden" (game/project.godot), as a bit value: the collision of shell parts that are never drawn
    /// ("drawn": false: a tree's climbing pole and crown caps). Bodies have it in their mask, so they climb and stand on those
    /// parts; everything that asks only for the world layer passes through them as through the leaves around them: sight,
    /// reach, sandbox placement and drops, the camera arms and the navigation bake.
    /// </summary>
    public const uint HiddenLayer = 16;
    /// <summary>What a body stands on, climbs and bumps into: the world and the hidden parts.</summary>
    public const uint BodyMask = WorldLayer | HiddenLayer;
    /// <summary>
    /// Physics layer 6, "far ground" (bit value 32, S12b): the land's coarse level where its walkable ground is not built
    /// (TerrainCollision keeps that near the bodies). Only sight and aim ask for it (SightMask), so far hills hide what is behind
    /// them and stop an aim; no body has it in its mask, and nothing that settles, supports or measures ground asks for it.
    /// </summary>
    public const uint FarGroundLayer = 32;
    /// <summary>What sight and aim rays test: the world, and the far ground where the walkable ground is not built.</summary>
    public const uint SightMask = WorldLayer | FarGroundLayer;

    public static Node3D Build(RoomData room)
    {
        var root = new Node3D { Name = "Room" };
        try
        {
            BuildInto(room, root);
            return root;
        }
        catch
        {
            // Nothing half built is left behind (a mesh that fails its check after the load, say).
            root.Free();
            throw;
        }
    }

    private static void BuildInto(RoomData room, Node3D root)
    {
        root.SetMeta("room_id", room.RoomId);
        root.SetMeta("room_manifest_sha256", room.ManifestSha256);
        var shell = new Node3D { Name = "Shell" };
        var objects = new Node3D { Name = "Objects" };
        root.AddChild(shell);
        root.AddChild(objects);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var grounds = new Dictionary<string, (Node3D Node, uint Layer)>(StringComparer.Ordinal);
        foreach (var part in room.Shell)
        {
            var node = BuildShellPart(room, part);
            shell.AddChild(node);
            if (part.Collides && room.Terrain?.Part(part.Id) != null) grounds[part.Id] = (node, part.Drawn ? WorldLayer : HiddenLayer);
        }
        // The tiled land collides through its tiles' level 0, built where the bodies are (TerrainCollision, S12b).
        if (TerrainCollision.Create(room, grounds) is { } ground) root.AddChild(ground);
        if (room.Terrain is { } terrain)
            GD.Print(System.FormattableString.Invariant(
                $"ROOM_TERRAIN {terrain.Parts.Count} tiled parts, {terrain.Parts.Sum(p => p.Tiles.Count)} tiles at {terrain.LevelCount} levels; the shell built in {clock.Elapsed.TotalMilliseconds:0} ms"));
        foreach (var instance in room.Objects) objects.AddChild(BuildObject(room, instance));
        // The 10x world's plants, trees and stones: drawn and collided per 8 m tile, built a few tiles a frame (PlantCopiesView).
        if (room.Copies != null) root.AddChild(PlantCopiesView.Create(room));

        // An island's loading leaves C#'s heap at about twice what it keeps: compacted once the room has arrived (LoadSettle).
        if (room.Copies != null || room.Terrain != null) root.AddChild(new LoadSettle { Name = LoadSettle.NodeName });
        // Everything that draws a file is built: the files' bytes go (a 10x island's are about 40 MB). A second build reads and
        // checks them again; the land's ground stays in the room's data for collision and navigation.
        room.ReleaseMeshBytes();
        // An island's reading leaves most of C#'s heap as garbage (the files, the manifest, the checks): compacted now, before
        // the navigation bake and the copies add theirs, the loading's peak is that much lower (S12b).
        if (room.Copies != null || room.Terrain != null) LoadSettle.Compact("the room's reading");
    }

    public static string NodeName(string entityId) =>
        string.Concat(entityId[(entityId.IndexOf(':') + 1)..].Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    private static Node3D BuildShellPart(RoomData room, ShellPart part)
    {
        if (room.Terrain?.Part(part.Id) is { } tiled) return BuildTiledPart(room, part, tiled);
        // The file is read (and checked again, after a build let its bytes go) before any body exists: a failure leaves nothing.
        var loaded = part.MeshPath != null ? LoadGlb(room, room.Directory + "/" + part.MeshPath, part.Id) : null;
        Node3D node = part.Collides ? new StaticBody3D { CollisionLayer = part.Drawn ? WorldLayer : HiddenLayer, CollisionMask = 0 } : new Node3D();
        node.Name = NodeName(part.Id);
        node.SetMeta("entity_id", part.Id);
        node.SetMeta("surface_role", part.Role);
        node.SetMeta("material_role", part.MaterialRole);
        node.SetMeta("drawn", part.Drawn);
        if (loaded is { } scene)
        {
            // A collision-only part ("drawn": false: a climbing pole in a tree's leaves, a cap to stand on) keeps its shapes
            // and never enters the scene as a visual, so no look or capture can show it.
            if (part.Drawn) node.AddChild(scene);
            if (part.Collides)
                foreach (var mesh in Meshes(scene))
                    node.AddChild(new CollisionShape3D { Shape = OneSided(mesh.Mesh), Transform = RelativeTransform(scene, mesh) });
            // Water is a query-only surface on its own layer (RoomWater): bodies swim in it and nothing collides with it.
            if (RoomWater.IsWater(part))
                node.AddChild(RoomWater.CreateCollider(Meshes(scene).Select(mesh => ((Shape3D)mesh.Mesh.CreateTrimeshShape(), RelativeTransform(scene, mesh))).ToList()));
            if (!part.Drawn) scene.Free();
            return node;
        }
        // The openings listed for this part are holes in the slab: a window in a solid wall is a hole, not a painted square.
        var visualHoles = room.Openings.Where(o => o.HostPartId == part.Id && o.CutsVisual).ToArray();
        var (arrayMesh, shape) = ExtrudePolygon(part.Points, part.ThicknessM, visualHoles, visualHoles.Where(o => o.CutsCollision).ToArray());
        if (part.Drawn) node.AddChild(new MeshInstance3D { Name = "Visual", Mesh = arrayMesh, MaterialOverride = MaterialLibrary.For(part.MaterialRole, part.BaseColor) });
        if (part.Collides) node.AddChild(new CollisionShape3D { Name = "Collision", Shape = shape });
        if (RoomWater.IsWater(part)) node.AddChild(RoomWater.CreateCollider(new[] { ((Shape3D)arrayMesh.CreateTrimeshShape(), Transform3D.Identity) }));
        return node;
    }

    /// <summary>Meta on a tiled terrain part's node: its tile count. Its collision is the island's walkable ground (IslandNavigation.GroundFaces gives the same triangles).</summary>
    public const string TerrainTilesMeta = "terrain_tiles";
    /// <summary>Meta on each of a tiled part's drawn meshes: the level of detail it is.</summary>
    public const string TerrainLevelMeta = "terrain_level";

    /// <summary>
    /// A tiled shell part (terrain_tiles, the 10x world): every tile's levels drawn, each only within its distance band
    /// (DrawDistances.TerrainBand), so one level per tile shows. Its collision is level 0 alone, one one-sided shape per tile, read
    /// from the pinned GLB's floats (TiledPart.GroundFaces) and built where the bodies are by TerrainCollision, whose hits name
    /// this node; a part that is never drawn is never imported at all. The part keeps its id, roles and metadata: supports,
    /// nature and the HUD see the shell part they always did.
    /// </summary>
    private static Node3D BuildTiledPart(RoomData room, ShellPart part, TiledPart tiled)
    {
        var drawn = part.Drawn ? LoadGlb(room, tiled.MeshPath, part.Id) : null;
        Node3D node = part.Collides ? new StaticBody3D { CollisionLayer = part.Drawn ? WorldLayer : HiddenLayer, CollisionMask = 0 } : new Node3D();
        node.Name = NodeName(part.Id);
        node.SetMeta("entity_id", part.Id);
        node.SetMeta("surface_role", part.Role);
        node.SetMeta("material_role", part.MaterialRole);
        node.SetMeta("drawn", part.Drawn);
        node.SetMeta(TerrainTilesMeta, tiled.Tiles.Count);
        var levels = room.Terrain!.LevelCount;
        if (drawn is { } scene)
        {
            foreach (var mesh in Meshes(scene))
            {
                // Every node of a tiled GLB is a tile (RoomData checked); one the importer renamed is drawn at every distance.
                var level = TiledPart.ParseNodeName(mesh.Name) is { } tile ? tile.Level : -1;
                if (level < 0) continue;
                var (begin, end) = DrawDistances.TerrainBand(level, levels);
                mesh.VisibilityRangeBegin = begin;
                mesh.VisibilityRangeBeginMargin = begin > 0 ? DrawDistances.TerrainMarginM : 0f;
                mesh.VisibilityRangeEnd = end;
                mesh.VisibilityRangeEndMargin = end > 0 ? DrawDistances.TerrainMarginM : 0f;
                mesh.SetMeta(TerrainLevelMeta, level);
            }
            node.AddChild(scene);
        }
        if (RoomWater.IsWater(part))
        {
            var ground = new Vector3[tiled.Tiles.Count][];
            System.Threading.Tasks.Parallel.For(0, ground.Length, i => ground[i] = tiled.GroundFaces(i));
            node.AddChild(RoomWater.CreateCollider(ground.Select(faces => ((Shape3D)new ConcavePolygonShape3D { Data = faces }, Transform3D.Identity)).ToList()));
        }
        return node;
    }

    private static Node3D BuildObject(RoomData room, ObjectInstance instance)
    {
        var asset = instance.Asset;
        var loaded = asset.GeometryKind == "primitive" ? null : LoadGlb(room, asset.Directory + "/" + asset.MeshPath, instance.Id);
        Node3D body = asset.CollisionKind == "none" ? new Node3D() : new StaticBody3D { CollisionLayer = WorldLayer, CollisionMask = 0 };
        body.Name = NodeName(instance.Id);
        body.Transform = new Transform3D(new Basis(instance.Rotation).Scaled(Vector3.One * instance.Scale), instance.PositionM);
        body.SetMeta("entity_id", instance.Id);
        body.SetMeta("asset_id", asset.AssetId);
        body.SetMeta("display_name", instance.DisplayName ?? asset.DisplayName);
        body.SetMeta("category", asset.Category);
        body.SetMeta("category_group", asset.CategoryGroup);
        body.SetMeta("tier", asset.Tier);
        body.SetMeta("provenance_kind", asset.ProvenanceKind);
        body.SetMeta("movable", asset.Movable);
        body.SetMeta("mass_kg", asset.MassKg);
        body.SetMeta("affordances", asset.Affordances);
        body.SetMeta("review_status", asset.ReviewStatus);
        body.SetMeta("support_kind", instance.SupportKind);
        if (instance.SupportTarget != null) body.SetMeta("support_target", instance.SupportTarget);
        var roles = new Godot.Collections.Dictionary();
        foreach (var slot in asset.Materials) roles[slot.Slot] = slot.Role;
        body.SetMeta("material_roles", roles);

        var size = asset.DimensionsM;
        if (asset.GeometryKind == "primitive")
        {
            var slot = asset.Materials[0];
            var (mesh, shape, centre) = Primitive(asset.Primitive!, size);
            body.AddChild(new MeshInstance3D { Name = "Visual", Mesh = mesh, Position = centre, MaterialOverride = MaterialLibrary.For(slot.Role, slot.BaseColor ?? new Color("b3aea4")) });
            if (asset.CollisionKind == "primitive") body.AddChild(new CollisionShape3D { Name = "Collision", Shape = shape, Position = centre });
            return body;
        }
        var visual = loaded!;
        visual.Name = "Visual";
        body.AddChild(visual);
        switch (asset.CollisionKind)
        {
            case "box":
                body.AddChild(new CollisionShape3D { Name = "Collision", Shape = new BoxShape3D { Size = size }, Position = Vector3.Up * size.Y * 0.5f });
                break;
            case "convex_hull":
            case "convex_decomposition":
                // A single hull until the capture pipeline ships decompositions; recorded so nobody mistakes it for the final shape.
                var points = Meshes(visual).SelectMany(m => m.Mesh.GetFaces().Select(v => RelativeTransform(visual, m) * v)).ToArray();
                body.AddChild(new CollisionShape3D { Name = "Collision", Shape = new ConvexPolygonShape3D { Points = points } });
                if (asset.CollisionKind == "convex_decomposition") body.SetMeta("collision_approximation", "single_convex_hull");
                break;
            case "trimesh_static":
                foreach (var mesh in Meshes(visual))
                    body.AddChild(new CollisionShape3D { Shape = OneSided(mesh.Mesh), Transform = RelativeTransform(visual, mesh) });
                break;
            case "none":
                break;
            default:
                // Never build a body that silently has no collision.
                throw new RoomLoadException($"{instance.Id}: collision kind '{asset.CollisionKind}' is not supported for mesh assets");
        }
        return body;
    }

    private static (Mesh Mesh, Shape3D Shape, Vector3 Centre) Primitive(string kind, Vector3 size)
    {
        var radius = Mathf.Min(size.X, size.Z) * 0.5f;
        return kind switch
        {
            "box" => (new BoxMesh { Size = size }, new BoxShape3D { Size = size }, Vector3.Up * size.Y * 0.5f),
            "cylinder" => (new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = size.Y }, new CylinderShape3D { Radius = radius, Height = size.Y }, Vector3.Up * size.Y * 0.5f),
            "sphere" => (new SphereMesh { Radius = radius, Height = radius * 2 }, new SphereShape3D { Radius = radius }, Vector3.Up * radius),
            "capsule" => (new CapsuleMesh { Radius = radius, Height = Mathf.Max(size.Y, radius * 2) }, new CapsuleShape3D { Radius = radius, Height = Mathf.Max(size.Y, radius * 2) }, Vector3.Up * Mathf.Max(size.Y, radius * 2) * 0.5f),
            _ => throw new RoomLoadException($"unsupported primitive '{kind}'"),
        };
    }

    /// <summary>Extrude a planar polygon away from the room. Inner face follows the polygon winding (normal into the room).</summary>
    public static (ArrayMesh Mesh, Shape3D Shape) ExtrudePolygon(Vector3[] points, float thickness) =>
        ExtrudePolygon(points, thickness, Array.Empty<ShellOpening>(), Array.Empty<ShellOpening>());

    /// <summary>
    /// Extrude a planar polygon away from the room with rectangular holes cut through it. The mesh has the visual holes
    /// (every opening that lets light and sight through); the collision shape has only the solid holes (those a body can
    /// pass), so a window is glass to a body and open to the sun. Geometry2D cannot triangulate a polygon with holes, so
    /// the wall is cut into simple pieces by clipping it with the four strips around each hole, and each piece is triangulated.
    /// </summary>
    public static (ArrayMesh Mesh, Shape3D Shape) ExtrudePolygon(Vector3[] points, float thickness, IReadOnlyList<ShellOpening> visualHoles, IReadOnlyList<ShellOpening> solidHoles)
    {
        var normal = NewellNormal(points);
        var back = -normal * thickness;
        var mesh = Slab(points, normal, back, visualHoles);
        if (solidHoles.Count > 0) return (mesh, Slab(points, normal, back, solidHoles).CreateTrimeshShape());
        var u = (points[1] - points[0]).Normalized();
        var v = normal.Cross(u).Normalized();
        var flat = points.Select(p => new Vector2((p - points[0]).Dot(u), (p - points[0]).Dot(v))).ToArray();
        // No hole a body can pass: the collision is the whole slab, glass included.
        Shape3D shape = IsConvex(flat)
            ? new ConvexPolygonShape3D { Points = points.Concat(points.Select(p => p + back)).ToArray() }
            : (visualHoles.Count == 0 ? mesh : Slab(points, normal, back, Array.Empty<ShellOpening>())).CreateTrimeshShape();
        return (mesh, shape);
    }

    /// <summary>The mesh of a slab: the polygon (minus the holes) on the inside, its mirror at the back, the edge faces round the outside and round each hole.</summary>
    private static ArrayMesh Slab(Vector3[] points, Vector3 normal, Vector3 back, IReadOnlyList<ShellOpening> holes)
    {
        var u = (points[1] - points[0]).Normalized();
        var v = normal.Cross(u).Normalized();
        Vector2 Flat(Vector3 p) => new((p - points[0]).Dot(u), (p - points[0]).Dot(v));
        Vector3 Lift(Vector2 f) => points[0] + u * f.X + v * f.Y;
        var flat = points.Select(Flat).ToArray();
        // Each hole is a rectangle in the plane of the wall: its width runs along the wall at the horizontal (up cross
        // normal), its height up the wall; a surface that is not upright (a ceiling vent) uses the polygon's own axes.
        var rectangles = new List<(Vector2 Centre, Vector2 A, Vector2 B, float Width, float Height)>();
        foreach (var hole in holes)
        {
            var upright = Mathf.Abs(normal.Dot(Vector3.Up)) < 0.99f;
            var across = upright ? Vector3.Up.Cross(normal).Normalized() : u;
            var up = upright ? normal.Cross(across).Normalized() : v;
            if (upright && up.Dot(Vector3.Up) < 0f) up = -up;
            var onPlane = hole.CenterM - normal * (hole.CenterM - points[0]).Dot(normal);
            rectangles.Add((Flat(onPlane), new Vector2(across.Dot(u), across.Dot(v)).Normalized(), new Vector2(up.Dot(u), up.Dot(v)).Normalized(), hole.SizeM.X, hole.SizeM.Y));
        }
        var pieces = new List<Vector2[]> { flat };
        // A hole that removes nothing gets no reveal: the test room's west wall is already built round its window, and the
        // window's host is the piece below the sill, whose neighbours' own edges are the reveal.
        var cut = new List<(Vector2 Centre, Vector2 A, Vector2 B, float Width, float Height)>();
        foreach (var r in rectangles)
        {
            var before = pieces.Sum(p => Mathf.Abs(Area(p)));
            const float big = 1000f;
            Vector2[] Strip(float x0, float x1, float y0, float y1) => new[]
            {
                r.Centre + r.A * x0 + r.B * y0, r.Centre + r.A * x1 + r.B * y0, r.Centre + r.A * x1 + r.B * y1, r.Centre + r.A * x0 + r.B * y1,
            };
            var halfW = r.Width * 0.5f;
            var halfH = r.Height * 0.5f;
            var strips = new[] { Strip(-big, -halfW, -big, big), Strip(halfW, big, -big, big), Strip(-halfW, halfW, -big, -halfH), Strip(-halfW, halfW, halfH, big) };
            var next = new List<Vector2[]>();
            foreach (var piece in pieces)
                foreach (var strip in strips)
                    foreach (var part in Geometry2D.IntersectPolygons(piece, strip))
                        if (part.Length >= 3 && Mathf.Abs(Area(part)) > 1e-6f) next.Add(part);
            pieces = next;
            if (before - pieces.Sum(p => Mathf.Abs(Area(p))) > 1e-4f) cut.Add(r);
        }
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var piece in pieces)
        {
            var triangles = Geometry2D.TriangulatePolygon(piece);
            if (triangles.Length == 0) throw new RoomLoadException("shell polygon cannot be triangulated");
            for (var i = 0; i < triangles.Length; i += 3)
            {
                var a = Lift(piece[triangles[i]]); var b = Lift(piece[triangles[i + 1]]); var c = Lift(piece[triangles[i + 2]]);
                Triangle(tool, a, b, c, normal);
                Triangle(tool, a + back, b + back, c + back, -normal);
            }
        }
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            var outward = (b - a).Cross(normal).Normalized();
            // Only the stretches of the edge that are still wall: an opening that runs off the edge (a passage at the end
            // of a wall) leaves no face standing across it.
            foreach (var (from, to) in OutsideHoles(flat[i], flat[(i + 1) % points.Length], cut))
            {
                var p = a + (b - a) * from; var q = a + (b - a) * to;
                Triangle(tool, p, q, q + back, outward);
                Triangle(tool, p, q + back, p + back, outward);
            }
        }
        // The reveal: faces through the thickness round each hole, facing into the opening, wherever the wall goes on
        // behind them. A door's sill on the wall's bottom edge, a side beyond the wall's end, or a side that falls inside
        // a neighbouring opening gets none.
        foreach (var r in cut)
        {
            var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) }
                .Select(c => r.Centre + r.A * (c.X * r.Width * 0.5f) + r.B * (c.Y * r.Height * 0.5f)).ToArray();
            var centre = Lift(r.Centre);
            for (var i = 0; i < 4; i++)
            {
                var side = (corners[i] + corners[(i + 1) % 4]) * 0.5f;
                var behind = side + (side - r.Centre).Normalized() * 0.005f;
                if (!pieces.Any(piece => Geometry2D.IsPointInPolygon(behind, piece))) continue;
                var a = Lift(corners[i]); var b = Lift(corners[(i + 1) % 4]);
                var inward = centre - (a + b) * 0.5f;
                inward = (inward - normal * inward.Dot(normal)).Normalized();
                Triangle(tool, a, b, b + back, inward);
                Triangle(tool, a, b + back, a + back, inward);
            }
        }
        return tool.Commit();
    }

    /// <summary>The stretches of the segment a to b, as fractions of its length, that lie outside every hole.</summary>
    private static List<(float From, float To)> OutsideHoles(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 Centre, Vector2 A, Vector2 B, float Width, float Height)> holes)
    {
        var keep = new List<(float From, float To)> { (0f, 1f) };
        foreach (var hole in holes)
        {
            // The part of the segment strictly inside the hole: inside both of its slabs, across and up.
            var (enter, leave) = (0f, 1f);
            foreach (var (axis, half) in new[] { (hole.A, hole.Width * 0.5f), (hole.B, hole.Height * 0.5f) })
            {
                var start = (a - hole.Centre).Dot(axis);
                var rate = (b - a).Dot(axis);
                if (Mathf.Abs(rate) < 1e-6f)
                {
                    // An edge along the hole's border (a door's foot on the wall's bottom edge) counts as inside it.
                    if (Mathf.Abs(start) > half + 1e-4f) (enter, leave) = (1f, 0f);
                    continue;
                }
                var t0 = (-half - start) / rate;
                var t1 = (half - start) / rate;
                enter = Mathf.Max(enter, Mathf.Min(t0, t1));
                leave = Mathf.Min(leave, Mathf.Max(t0, t1));
            }
            if (leave - enter < 1e-5f) continue;
            keep = keep.SelectMany(k => new[] { (From: k.From, To: Mathf.Min(k.To, enter)), (From: Mathf.Max(k.From, leave), To: k.To) })
                .Where(k => k.To - k.From > 1e-5f).ToList();
        }
        return keep;
    }

    private static float Area(Vector2[] polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++) sum += polygon[i].Cross(polygon[(i + 1) % polygon.Length]);
        return sum * 0.5f;
    }

    /// <summary>
    /// Mesh collision is one-sided (contracts/README.md): a body meets a face only from its front, so a climber coming up
    /// inside a tree's crown passes up through the cap and stands on top of it. Godot's default, set explicitly here.
    /// </summary>
    public static ConcavePolygonShape3D OneSided(Mesh mesh)
    {
        var shape = (ConcavePolygonShape3D)mesh.CreateTrimeshShape();
        shape.BackfaceCollision = false;
        return shape;
    }

    /// <summary>Godot treats clockwise triangles (seen from the front) as front faces.</summary>
    private static void Triangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        if ((b - a).Cross(c - a).Dot(normal) > 0) (b, c) = (c, b);
        foreach (var vertex in new[] { a, b, c })
        {
            tool.SetNormal(normal);
            tool.AddVertex(vertex);
        }
    }

    public static Vector3 NewellNormal(IReadOnlyList<Vector3> points)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i]; var q = points[(i + 1) % points.Count];
            n += new Vector3((p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
        }
        if (n.LengthSquared() < 1e-12f) throw new RoomLoadException("shell polygon is degenerate");
        return n.Normalized();
    }

    private static bool IsConvex(Vector2[] polygon)
    {
        var sign = 0;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length]; var c = polygon[(i + 2) % polygon.Length];
            var cross = (b - a).Cross(c - b);
            if (Mathf.Abs(cross) < 1e-9f) continue;
            var current = cross > 0 ? 1 : -1;
            if (sign != 0 && current != sign) return false;
            sign = current;
        }
        return true;
    }

    /// <summary>
    /// Builds from bytes RoomData verified against the manifest (kept from the load, or read and checked again after a build let
    /// them go), with no base path, so nothing outside the pinned file can load.
    /// </summary>
    internal static Node3D LoadGlb(RoomData room, string path, string label)
    {
        var bytes = room.MeshBytes(path) ?? throw new RoomLoadException($"{label}: mesh {path} was not verified at load");
        // Released as soon as the scene exists: the importer's state holds every array it read (a 10x island's terrain is
        // hundreds of megabytes), which would otherwise wait for the garbage collector to finalize the wrappers.
        using var document = new GltfDocument();
        using var state = new GltfState();
        var error = document.AppendFromBuffer(bytes, "", state);
        if (error != Error.Ok) throw new RoomLoadException($"{label}: mesh {path} could not be read ({error})");
        return document.GenerateScene(state) as Node3D ?? throw new RoomLoadException($"{label}: mesh {path} has no 3D scene");
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh) yield return mesh;
        foreach (var child in node.GetChildren())
            foreach (var nested in Meshes(child)) yield return nested;
    }

    /// <summary>Transform of a descendant relative to an ancestor, valid before the nodes enter the tree.</summary>
    private static Transform3D RelativeTransform(Node3D ancestor, Node3D descendant)
    {
        var result = Transform3D.Identity;
        for (Node? node = descendant; node != null && node != ancestor.GetParent(); node = node.GetParent())
            if (node is Node3D spatial) result = spatial.Transform * result;
        return result;
    }
}
