using Godot;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Decision 95 (the founder, 17 October): a bubble lasts 8 seconds, not 20; the boat keeps its minute. The player's controls name the
/// Bubble's own time when they name none (BubbleLaws): the nature.cast controls (BubbleCastArgs) and the Bubbles key (PlayerEffect, by
/// what the place makes of the spot), never the pack's duration_default_s (20 s in v5), which the key sent for every bubble before.
/// </summary>
public partial class NatureTest
{
    private async Task TestBubbleTime()
    {
        await NewWorld(SavePath("bubble-time"));
        _world!.AddChild(Pool());
        await Frames(2);
        Check(_companion.TryTeleportTo(new Vector3(-0.2f, 0.01f, 0.7f)), "(the Gubble floats by the pool)");
        await Frames(2);
        static double? Seconds(JsonObject result) => result["data"]?["duration_s"]?.GetValue<double>();
        static string? Kind(JsonObject result) => result["data"]?["kind"]?.GetValue<string>();
        var boat = _host.PlayerEffect("bubbles", new Vector3(-0.8f, PoolY, 0.7f));
        var dry = _host.PlayerEffect("bubbles", new Vector3(0.4f, 0.01f, 0.7f));
        var carry = _host.PlayerBubblePreview("bubble_carry", _player.GlobalPosition, CommandHost.PlayerAvatar);
        Check(Ok(boat) && Kind(boat) == "boat" && Seconds(boat) == 60 && BubbleLaws.BoatDurationDefaultS == 60,
            "the Bubbles key on water makes a boat, which keeps its minute: " + Canonical(boat["data"] ?? boat["error"]));
        Check(Ok(dry) && Kind(dry) != "boat" && Seconds(dry) == 8 && BubbleLaws.CarryDurationDefaultS == 8,
            "the Bubbles key on dry ground makes a bubble of 8 seconds (decision 95), not the pack's 20: " + Canonical(dry["data"] ?? dry["error"]));
        Check(Ok(carry) && Seconds(carry) == 8, "a carry the player's nature.cast controls preview, naming no time, lasts 8 seconds too: " + Canonical(carry["data"] ?? carry["error"]));
    }
}
