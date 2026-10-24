using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Room;

/// <summary>
/// What navigation needs from the 10x world (packet S6, for S7's island-scale navigation), in one place and from the room's data
/// alone, so it can be read on a bake thread without walking the scene:
/// the walkable ground (every drawn, colliding tiled terrain part's level-0 triangles, the very faces its collision is made of), and the
/// copies' obstacles (each tree's trunk and each solid stone, as a footprint with a bottom and a height). A tree's hidden pole and
/// caps are not here: navigation ignores the hidden layer, as it always did. Everything else (objects, parts drawn whole) is still
/// collision in the scene, on the world layer, as before.
///
/// Read it as: <c>IslandNavigation.AddTo(room, source, area)</c> for a bake's source geometry, or GroundFaces and Obstacles for
/// the pieces. All of it is pure C# over RoomData (no node, no server): safe on any thread, and the same answer every time.
/// In the scene, a tiled part's node carries RoomBuilder.TerrainTilesMeta: a parser that takes the ground from here skips those.
/// RoomNavigation does (given the room): it reads the ground once (GroundTiles) and adds each tile's obstructions at every bake.
/// </summary>
public static class IslandNavigation
{
    /// <summary>A copy navigation must keep off: its entity id, its outline seen from above (counter-clockwise) at BottomM, and how tall it stands.</summary>
    public sealed record Obstacle(string EntityId, Vector3[] Outline, float BottomM, float HeightM);

    /// <summary>The level-0 triangles (three corners each, Godot's winding, room metres) of every drawn, colliding tiled part's 16 m tiles that overlap area (x, z).</summary>
    public static Vector3[] GroundFaces(RoomData room, Rect2 area)
    {
        var faces = new List<Vector3>();
        foreach (var (tiled, tile) in GroundTiles(room))
        {
            var (tx, tz, _) = tiled.Tiles[tile];
            if (!new Rect2(tx * TerrainTiles.TileM, tz * TerrainTiles.TileM, TerrainTiles.TileM, TerrainTiles.TileM).Intersects(area, includeBorders: true)) continue;
            faces.AddRange(tiled.GroundFaces(tile));
        }
        return faces.ToArray();
    }

    /// <summary>
    /// The walkable ground's 16 m tiles, each read with its part's GroundFaces(tile): every tile of every tiled part that collides
    /// on the world layer (drawn; a part that is not drawn collides on the hidden layer, which navigation ignores). For a caller
    /// that reads the ground for a bake (RoomNavigation reads only the tiles under the tiles it bakes, and keeps none: S12b).
    /// </summary>
    public static List<(TiledPart Part, int Tile)> GroundTiles(RoomData room)
    {
        var tiles = new List<(TiledPart, int)>();
        if (room.Terrain is not { } terrain) return tiles;
        foreach (var tiled in terrain.Parts)
        {
            if (room.Shell.FirstOrDefault(p => p.Id == tiled.PartId) is not { Collides: true, Drawn: true }) continue;
            for (var i = 0; i < tiled.Tiles.Count; i++) tiles.Add((tiled, i));
        }
        return tiles;
    }

    /// <summary>
    /// The trunks and solid stones whose footprint overlaps area: a trunk as its base ring (trunk_radius_m, the widest it is) from
    /// the copy's base up to trunk_top_m; a stone as its hull's outline from its lowest point to its top_m.
    /// </summary>
    public static List<Obstacle> Obstacles(RoomData room, Rect2 area)
    {
        var obstacles = new List<Obstacle>();
        if (room.Copies is not { } copies) return obstacles;
        // A copy stands in its tile but may reach a little past it: look one tile further round the area.
        var grown = area.Grow(PlantCopies.TileM);
        foreach (var set in copies.Sets)
        {
            var tree = set.Kind == PlantKind.Tree;
            if (!tree && set.Collides != "solid") continue;
            for (var t = 0; t < set.TileCount; t++)
            {
                var (tx, tz, first, count) = set.Tile(t);
                if (!new Rect2(tx * PlantCopies.TileM, tz * PlantCopies.TileM, PlantCopies.TileM, PlantCopies.TileM).Intersects(grown, includeBorders: true)) continue;
                for (var row = first; row < first + count; row++)
                {
                    Obstacle obstacle;
                    if (tree)
                    {
                        var shape = set.Tree(row);
                        var ring = PlantCollision.Trunk(shape).Take(PlantCollision.Segments).ToArray();
                        obstacle = new Obstacle(PlantCopies.IdOf(set.Name, row), PlantCollision.Footprint(ring, shape.BaseY), shape.BaseY, shape.TrunkTopM - shape.BaseY);
                    }
                    else
                    {
                        if (set.SolidPoints.Count == 0) continue;
                        var hull = PlantCollision.Solid(set, row);
                        var bottom = hull.Min(p => p.Y);
                        obstacle = new Obstacle(PlantCopies.IdOf(set.Name, row), PlantCollision.Footprint(hull, bottom), bottom, set.TopM(row) - bottom);
                    }
                    if (Overlaps(obstacle.Outline, area)) obstacles.Add(obstacle);
                }
            }
        }
        return obstacles;
    }

    /// <summary>
    /// Both into a bake's source geometry: the ground as faces (unless ground is false, for a parser that takes the tiled parts from
    /// the scene or keeps the ground itself, as RoomNavigation does) and every obstacle as a projected obstruction, so nothing under or inside a trunk or a stone is walkable.
    /// NavigationMeshSourceGeometryData3D takes additions from any thread. Returns the number of obstructions added.
    /// </summary>
    public static int AddTo(RoomData room, NavigationMeshSourceGeometryData3D source, Rect2 area, bool ground = true)
    {
        if (ground)
        {
            var faces = GroundFaces(room, area);
            if (faces.Length > 0) source.AddFaces(faces, Transform3D.Identity);
        }
        var obstacles = Obstacles(room, area);
        foreach (var obstacle in obstacles)
            source.AddProjectedObstruction(obstacle.Outline, obstacle.BottomM, obstacle.HeightM, false);
        return obstacles.Count;
    }

    private static bool Overlaps(Vector3[] outline, Rect2 area)
    {
        if (outline.Length == 0) return false;
        float x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), z0 = outline.Min(p => p.Z), z1 = outline.Max(p => p.Z);
        return new Rect2(x0, z0, x1 - x0, z1 - z0).Intersects(area, includeBorders: true);
    }
}
