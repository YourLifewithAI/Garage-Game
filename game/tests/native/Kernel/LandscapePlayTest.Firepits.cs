using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The generator's firepits on a generated island (decision 74; CommandHostFirepits): the exported manifest's x_firepits are the
/// island's firepits, every one kept (the island's own ground holds each), numbered in their order; the player stands beside each
/// and the rest key says it is not lit yet; the Gubble's Fire lights the first and the player sits down there, their camp now.
/// </summary>
public partial class LandscapePlayTest
{
    private async Task TestFirepitsFromTheGenerator(string directory)
    {
        var manifest = JsonNode.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(directory) + "/room.json"))!.AsObject();
        if (manifest["extensions"]?["x_firepits"]?["items"] is not JsonArray items)
        {
            Check(_host.CampSites.Count > 1, "an island exported without x_firepits has the kernel's spread of firepits");
            return;
        }
        var listed = items.Select(i => i!["position_m"]!.AsArray()).Select(p => new Vector3(p[0]!.GetValue<float>(), p[1]!.GetValue<float>(), p[2]!.GetValue<float>())).ToList();
        var pits = _host.CampSites.Where(c => !c.Starting).ToList();
        Check(listed.Count > 0 && pits.Select(c => c.Id).SequenceEqual(listed.Select((_, n) => $"firepit:{n + 1}")) &&
              pits.Zip(listed).All(p => PlanarDistance(p.First.PositionM, p.Second) < 0.01f && Mathf.Abs(p.First.PositionM.Y - p.Second.Y) <= CampLaws.FirepitGroundM),
            $"the island's firepits are the generator's: all {listed.Count} of the manifest's kept on the island's ground, numbered in their order");
        var start = _host.StartingCampfire!.PositionM;
        var apart = pits.SelectMany((a, n) => pits.Skip(n + 1).Select(b => PlanarDistance(a.PositionM, b.PositionM))).DefaultIfEmpty(float.MaxValue).Min();
        Check(apart >= 4f - 0.01f && pits.All(p => PlanarDistance(p.PositionM, start) >= 3f - 0.01f) &&
              pits.All(p => _host.AllFractals.All(f => PlanarDistance(p.PositionM, f.PositionM) >= CampLaws.FirepitFractalClearM - 0.01f)),
            $"every two at least 4 m apart (the nearest {apart:0.0} m), 3 m from the starting campfire and 1 m from every Fractal");
        var player = _world.Player;
        var companion = _world.Companion;
        var unlit = 0;
        foreach (var pit in pits)
        {
            if (!await StandBeside(player, pit.PositionM)) continue;
            if (_host.PlayerCanRest().Words == CommandHost.RestUnlit) unlit++;
        }
        Check(unlit == pits.Count, $"the player stands beside each firepit and the rest key says it is not lit yet ({unlit} of {pits.Count})");
        // The Gubble's Fire lights the first; the player sits down by it, and it is their camp.
        var first = pits[0];
        _host.LearnFireForTests();
        _host.SetGubbleFire(true);
        var beside = await StandBeside(player, first.PositionM);
        var floats = beside && companion.TryTeleportTo(first.PositionM + new Vector3(0, 0.02f, -0.3f));
        await Frames(10);
        var at = new JsonArray(first.PositionM.X, first.PositionM.Y + 0.01f, first.PositionM.Z);
        var preview = Send(Preview(Command(NextId("fire-preview"), "nature.cast", new JsonObject { ["capability"] = "fire", ["center_m"] = at, ["radius_m"] = 0.05, ["duration_s"] = 6 })), Player);
        var token = preview["data"]?["preview_id"]?.GetValue<string>();
        var lit = Send(Command(NextId("fire"), "nature.cast", new JsonObject { ["capability"] = "fire", ["center_m"] = at.DeepClone(), ["radius_m"] = 0.05, ["duration_s"] = 6, ["preview_id"] = token }), Player);
        Check(floats && Ok(lit) && _host.IsCampfireLit(first.Id) && preview["data"]?["kind"]?.GetValue<string>() == FireKindWords.Firepit,
            $"the Gubble's Fire on {first.Id} lights it: {Code(lit) ?? "lit"} {lit["error"]?["message"]?.GetValue<string>()}");
        await Frames(3);
        var rest = _host.PlayerRest();
        Check(rest.Ok && rest.Campfire == first.Id && _host.HomeCamp == first.Id && _host.PlayerAtCamp(), $"the player sits down by it, and it is their camp: {rest.Words}");
        player.TryTeleportTo(_world.Room.SpawnFor("player").PositionM);
        companion.TryTeleportTo(_world.Room.SpawnFor("companion", _world.Room.SpawnFor("player")).PositionM);
    }

    private static JsonObject Preview(JsonObject command)
    {
        command["preview"] = true;
        return command;
    }

    /// <summary>The player stands on the ground beside a campfire's spot, in its resting ring and out of its flames (the first side that fits).</summary>
    private async Task<bool> StandBeside(GarageGame.Native.SmallPlayerController player, Vector3 spot)
    {
        for (var k = 0; k < 8; k++)
        {
            var a = k * Mathf.Pi / 4;
            if (!player.TryTeleportTo(spot + new Vector3(Mathf.Cos(a) * 0.15f, 0.01f, Mathf.Sin(a) * 0.15f))) continue;
            await Frames(4);
            if (player.IsOnFloor() && !player.IsSwimming && PlanarDistance(player.GlobalPosition, spot) <= CampLaws.RestRadiusM) return true;
        }
        return false;
    }
}
