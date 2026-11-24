using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native.Look.Intro;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>MENU-5, the land's picture: the room drawn as land, counted as the menu test counts it (cliff bands, landforms, cliff-side things, things on tops, bays, notches, no sea).</summary>
public partial class MenuTest
{
    /// <summary>A 10 by 8 m room: four walls; a door in the south wall, a window in the north wall; a table, a box on the table, a cabinet against the north wall, a crate in the open.</summary>
    private const string LandRoom = """
        {"bounds":{"min_m":[-5,0,-4],"max_m":[5,3,4]},
         "shell":{"parts":[
           {"id":"shell:floor","role":"floor","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,0,4],[-5,0,4]],"thickness_m":0.1}},
           {"id":"shell:wall_n","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,3,-4],[-5,3,-4]],"thickness_m":0.1}},
           {"id":"shell:wall_s","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,4],[5,0,4],[5,3,4],[-5,3,4]],"thickness_m":0.1}},
           {"id":"shell:wall_e","role":"wall","geometry":{"kind":"polygon","points_m":[[5,0,-4],[5,0,4],[5,3,4],[5,3,-4]],"thickness_m":0.1}},
           {"id":"shell:wall_w","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[-5,0,4],[-5,3,4],[-5,3,-4]],"thickness_m":0.1}}],
          "openings":[
           {"id":"door","kind":"door","host_part_id":"shell:wall_s","center_m":[2,1,4],"size_m":[1,2]},
           {"id":"win","kind":"window","host_part_id":"shell:wall_n","center_m":[-2,1.6,-4],"size_m":[1.2,1]}]},
         "objects":[{"id":"table","position_m":[0,0,0],"size_m":[2,1,1.2],"yaw_deg":0,"colours":[{"hex":"#aa5533"}]},
                    {"id":"box","position_m":[0.2,1,0],"size_m":[0.4,0.4,0.4],"yaw_deg":20,"colours":[{"hex":"#3366aa"}]},
                    {"id":"cabinet","position_m":[2,0,-3.7],"size_m":[1,2,0.5],"yaw_deg":0,"colours":[{"hex":"#665544"}]},
                    {"id":"crate","position_m":[-2,0,2],"size_m":[0.6,0.6,0.6],"yaw_deg":45}]}
        """;

    private static IntroSource LandSource()
    {
        using var document = JsonDocument.Parse(LandRoom);
        return IntroSource.Parse(document.RootElement);
    }

    private async Task TestLandPicture()
    {
        var source = LandSource();
        var land = LandPicture.Build(source);
        var counts = land.Counts;
        Check(counts == new LandCounts(Bands: 4, Landforms: 2, CliffSide: 1, Tops: 1, Bays: 1, Notches: 1, Sea: 0),
            $"the room is drawn as land and counted: four cliff bands, two landforms in the open, one cliff-side thing, one thing on a top, a bay for the door, a notch for the window, no sea ({counts})");
        Check(land.Forms.Count == source.Boxes.Count && land.Forms.FirstOrDefault(f => f.Colour == new Color("3366aa"))?.Stands == "top" && land.Forms.FirstOrDefault(f => f.Colour == new Color("665544"))?.Stands == "cliff",
            "each thing stands where it is: the box on the table is a top, the cabinet against the wall is cliff");
        Check(System.Math.Abs(land.BandM - 0.32f) < 1e-4f && land.Bands.All(b => b.Inward.Dot(new Vector2(0, 0) - (b.A + b.B) * 0.5f) > 0),
            "a cliff band is 4% of the land's short side, and every band leans in toward the floor");

        // A land made with the rim: its own openings stand in, and it still draws no sea.
        var rim = LandPicture.Build(source, new List<RimOpening> { new("notch", new Vector2(-2, -4), 1.2f), new("bay", new Vector2(2, 4), 1f), new("bay", new Vector2(-5, 0), 1f) });
        Check(rim.FromRim && rim.Counts.Bays == 2 && rim.Counts.Notches == 1 && rim.Counts.Sea == 0 && rim.Counts.Bands == 4, $"a rim record's notches and bays are the land's ({rim.Counts})");

        // A shape with nothing in it still draws; the picture's counts are the plan's.
        var thumbnail = new IslandThumbnail { Size = new Vector2(246, 128), CustomMinimumSize = new Vector2(246, 128) };
        AddChild(thumbnail);
        thumbnail.SetShape(source);
        await Frames(3);
        Check(thumbnail.Land != null && thumbnail.FloorTriangles == 2 && thumbnail.WallStrokes == 4 && thumbnail.BoxesDrawn == 4,
            $"the card's picture draws the plan ({thumbnail.FloorTriangles} floor triangles, {thumbnail.WallStrokes} cliff bands, {thumbnail.BoxesDrawn} things)");
        thumbnail.SetShape(null);
        await Frames(2);
        Check(!thumbnail.HasShape && thumbnail.Land == null && thumbnail.BoxesDrawn == 0, "a room without shape data draws the stand-in");
        thumbnail.QueueFree();

        // Names: cleaned as display text, then freed of "Landscape".
        Check(IslandNames.Land("Landscape: My   Garage\u0007", "x") == "My Garage" && IslandNames.Land("Landscape", "landscape_garage_nominal") == "Garage nominal" &&
              IslandNames.Land("", "test_room") == "Test room" && IslandNames.Land(new string('a', 90), "x").Length == IslandEntry.MaxNameLength,
            "a land's name is cleaned for display: no control characters, no \"Landscape\", the room's id spoken plainly when it has none, never over forty characters");
    }
}
