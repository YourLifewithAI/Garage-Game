using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests;

/// <summary>
/// M8's receipt.mark on the Bubble's receipts (decisions 14 and 60: the player marks the result, which teaches the AI the wanted
/// outcome): the Gubble's own bubble and its lead are marked like any receipt of the Gubble's, each once, in Challenge moving Trust.
/// </summary>
public partial class BubbleTest
{
    private async Task TestMarksOnTheBubble()
    {
        await NewWorld("trust-bubble", mode: "challenge", pips: 8);
        var feet = _player.GlobalPosition;
        var bubble = CastTheBubble(Companion, feet + new Vector3(0.3f, 0, 0.1f));
        var led = Lead(Companion, BubbleOf(bubble), feet + new Vector3(0.6f, 0, -0.3f));
        var ended = await UntilLeadEnds(Companion, led);
        var yes = Send(Command(NextId("mark"), "receipt.mark", new JsonObject { ["action_id"] = led["action_id"]!.GetValue<string>(), ["mark"] = "yes" }), Player);
        var notThat = Send(Command(NextId("mark"), "receipt.mark", new JsonObject { ["action_id"] = bubble["action_id"]!.GetValue<string>(), ["mark"] = "not_that" }), Player);
        var twice = Send(Command(NextId("mark"), "receipt.mark", new JsonObject { ["action_id"] = led["action_id"]!.GetValue<string>(), ["mark"] = "not_that" }), Player);
        var byTheGubble = Send(Command(NextId("mark"), "receipt.mark", new JsonObject { ["action_id"] = led["action_id"]!.GetValue<string>(), ["mark"] = "yes" }), Companion);
        var lessons = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion)["data"]?["entries"]?.AsArray()
            .Select(e => e?["line"]?.GetValue<string>() ?? "").ToList() ?? new();
        Check(Ok(bubble) && Ok(led) && ended == "succeeded" && Ok(yes) && Ok(notThat) && Code(twice) == "invalid_args" && Code(byTheGubble) == "permission_denied" &&
              _host.TrustCount == TrustLaws.MarkYes + TrustLaws.MarkNotThat && lessons.Any(l => l.StartsWith("You marked the Gubble's bubble lead", StringComparison.Ordinal) && l.EndsWith(": yes, like that.", StringComparison.Ordinal)) &&
              lessons.Any(l => l.StartsWith("You marked the Gubble's bubble", StringComparison.Ordinal) && !l.Contains("lead", StringComparison.Ordinal) && l.EndsWith(": not that.", StringComparison.Ordinal)),
            "the player marks the Gubble's bubble and its lead like any receipt of the Gubble's, each once; the Gubble marks nothing: " + string.Join(" | ", lessons));
    }
}
