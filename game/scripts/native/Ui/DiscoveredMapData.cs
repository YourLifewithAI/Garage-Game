using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The team's discovered map as the host exports it (CommandHost.ExportDiscovered): discovered ground as strips of cells, each
/// with the height of the level it lies on, and the things last seen, as boxes seen from above. Read by the journal's map and by
/// the minimap in the HUD's corner, which both draw from this and nothing else, so neither can show what no avatar has seen.
/// </summary>
public sealed class DiscoveredMapData
{
    /// <summary>Each run of discovered cells in a row, as a rectangle in metres (x, z) with the height of its level.</summary>
    public List<(Rect2 Area, float Height)> Land { get; } = new();
    /// <summary>Each discovered thing's footprint seen from above, with its name (untrusted, shown literally).</summary>
    public List<(Rect2 Area, string Name)> Objects { get; } = new();

    /// <summary>Read the host's discovered block, replacing what was here.</summary>
    public void Read(JsonObject data)
    {
        Land.Clear();
        Objects.Clear();
        var cell = (float)data["cell_m"]!.GetValue<double>();
        foreach (var level in data["levels"]!.AsArray().OfType<JsonObject>())
        {
            var x = (float)level["min_xz_m"]![0]!.GetValue<double>();
            var z = (float)level["min_xz_m"]![1]!.GetValue<double>();
            var columns = level["columns"]!.GetValue<int>();
            var rows = level["rows"]!.GetValue<int>();
            var height = (float)level["height_m"]!.GetValue<double>();
            var bits = Convert.FromBase64String(level["cells"]!.GetValue<string>());
            // Each row's runs of discovered cells become one strip. A whole byte of unseen cells (or of seen ones) is passed in one step.
            for (var r = 0; r < rows; r++)
            {
                var rowStart = r * columns;
                var c = 0;
                while (c < columns)
                {
                    var index = rowStart + c;
                    var bits8 = bits[index >> 3];
                    if ((index & 7) == 0 && bits8 == 0) { c += 8; continue; }
                    if ((bits8 & (1 << (index & 7))) == 0) { c++; continue; }
                    var start = c++;
                    while (c < columns)
                    {
                        index = rowStart + c;
                        bits8 = bits[index >> 3];
                        if ((index & 7) == 0 && bits8 == 0xFF && c + 8 <= columns) { c += 8; continue; }
                        if ((bits8 & (1 << (index & 7))) == 0) break;
                        c++;
                    }
                    Land.Add((new Rect2(x + start * cell, z + r * cell, (c - start) * cell, cell), height));
                }
            }
        }
        foreach (var known in data["entities"]!.AsObject())
        {
            var entity = known.Value!["entity"]!;
            var low = Point(entity["bounds_m"]!["min_m"]!);
            var high = Point(entity["bounds_m"]!["max_m"]!);
            Objects.Add((new Rect2(low.X, low.Z, high.X - low.X, high.Z - low.Z), entity["display_name"]!.GetValue<string>()));
        }
    }

    /// <summary>A point of the host's JSON ([x, y, z] in metres).</summary>
    public static Vector3 Point(JsonNode node) => new((float)node[0]!.GetValue<double>(), (float)node[1]!.GetValue<double>(), (float)node[2]!.GetValue<double>());
}
