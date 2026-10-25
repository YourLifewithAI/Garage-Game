using Godot;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Decision 95 (the founder, 17 October): a bubble lasts 8 seconds, not 20; decision 112 (25 October): a boat lasts 20 seconds, where
/// the 60 s default had crept in. The player's controls name the Bubble's own time when they name none (BubbleLaws): the nature.cast
/// controls (BubbleCastArgs) and the Bubbles key (PlayerEffect, by what the place makes of the spot), never the pack's
/// duration_default_s (20 s in v5), which the key sent for every bubble before. A cast that names its own time is bounded by the pack's
/// duration_max_s (60 s in v5), the boat's included.
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
        var water = new Vector3(-0.8f, PoolY, 0.7f);
        var boat = _host.PlayerEffect("bubbles", water);
        var boatExpires = boat["data"]?["expires_utc"]?.GetValue<string>();
        var cast = _host.PlayerBubbleCast("bubble_boat", water);
        var named = _host.PlayerBubbleCast("bubble_boat", water, duration: 60);
        var past = _host.PlayerBubbleCast("bubble_boat", water, duration: 61);
        var dry = _host.PlayerEffect("bubbles", new Vector3(0.4f, 0.01f, 0.7f));
        var carry = _host.PlayerBubblePreview("bubble_carry", _player.GlobalPosition, CommandHost.PlayerAvatar);
        Check(Ok(boat) && Kind(boat) == "boat" && Seconds(boat) == 20 && BubbleLaws.BoatDurationDefaultS == 20 &&
              boatExpires == KernelJson.Utc(_now + System.TimeSpan.FromSeconds(20)),
            "the Bubbles key on water makes a boat of 20 seconds (decision 112), not the minute that had crept in: " + Canonical(boat["data"] ?? boat["error"]));
        Check(Ok(cast) && Seconds(cast) == 20 && cast["data"]?["capability"]?.GetValue<string>() == "bubble_boat",
            "the player's nature.cast boat, naming no time, lasts 20 seconds too: " + Canonical(cast["data"] ?? cast["error"]));
        Check(Ok(named) && Seconds(named) == 60 && !Ok(past) && past["error"]?["code"]?.GetValue<string>() == "invalid_args" &&
              past["error"]?["allowed"]?.GetValue<double>() == 60,
            "a boat that names its own time is still bounded by the pack's duration_max_s, 60 s: " + Canonical(past["error"]));
        Check(Ok(dry) && Kind(dry) != "boat" && Seconds(dry) == 8 && BubbleLaws.CarryDurationDefaultS == 8,
            "the Bubbles key on dry ground makes a bubble of 8 seconds (decision 95), not the pack's 20: " + Canonical(dry["data"] ?? dry["error"]));
        Check(Ok(carry) && Seconds(carry) == 8, "a carry the player's nature.cast controls preview, naming no time, lasts 8 seconds too: " + Canonical(carry["data"] ?? carry["error"]));
    }
}
