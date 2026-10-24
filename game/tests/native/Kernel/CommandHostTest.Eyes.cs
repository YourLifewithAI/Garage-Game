using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Gubble's eyes (view.look) headless: the refusals in their order (actor, the player's setting, the rate, no renderer),
/// the setting read from the player's profile, the encoding step under its byte cap, the layer that keeps the Gubble's own
/// body out of its eye, and the facts' arithmetic. A real picture needs a renderer: the windowed check is in the lane report.
/// </summary>
public partial class CommandHostTest
{
    private async Task TestEyes()
    {
        TestEyeEncoding();
        TestEyeLayers();
        TestEyesSetting();
        TestEyeFacts();
        await TestLookRefusals();
    }

    private async Task<JsonObject> Look(JsonObject args, string principal = Companion)
    {
        _now += TimeSpan.FromMilliseconds(50);
        var message = new JsonObject { ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "look-" + (++_dumped), ["room_id"] = "test_room", ["op"] = "view.look", ["args"] = args };
        var result = await _host.HandleObjectAsync(message.ToJsonString(), principal);
        if (_dump != null)
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dump, $"{++_dumped:0000}_result.json"), CanonicalJson.Bytes(result));
        return result;
    }

    private static JsonObject Gubble(double? yaw = null, double? pitch = null, string actor = CommandHost.CompanionAvatarId)
    {
        var args = new JsonObject { ["actor"] = actor };
        if (yaw != null) args["yaw_deg"] = yaw;
        if (pitch != null) args["pitch_deg"] = pitch;
        return args;
    }

    private void TestEyeEncoding()
    {
        // Noise is JPEG's worst case: the picture must still come in under the cap, smaller or plainer as needed.
        var bytes = new byte[1024 * 576 * 3];
        new Random(7).NextBytes(bytes);
        var noise = Image.CreateFromData(1024, 576, false, Image.Format.Rgb8, bytes);
        var picture = EyeView.Encode(noise, CommandHost.LookImageMaxBytes);
        Check(picture != null && picture.Bytes.Length <= CommandHost.LookImageMaxBytes && picture.MediaType == "image/jpeg"
            && Math.Max(picture.WidthPx, picture.HeightPx) <= EyeView.MaxEdgePx && picture.WidthPx * 9 == picture.HeightPx * 16,
            $"a noisy picture is encoded within the {CommandHost.LookImageMaxBytes}-byte cap, at most 512 px, keeping its shape ({picture?.Bytes.Length} bytes, {picture?.WidthPx}x{picture?.HeightPx})");
        var decoded = new Image();
        Check(picture != null && decoded.LoadJpgFromBuffer(picture.Bytes) == Error.Ok && decoded.GetWidth() == picture.WidthPx && decoded.GetHeight() == picture.HeightPx,
            "the encoded picture decodes to the size it reports");
        var plain = Image.CreateEmpty(512, 288, false, Image.Format.Rgba8);
        plain.Fill(new Color(0.4f, 0.6f, 0.8f));
        var calm = EyeView.Encode(plain, CommandHost.LookImageMaxBytes);
        Check(calm != null && calm.WidthPx == 512 && calm.HeightPx == 288 && calm.Bytes.Length < 20000, "a plain picture keeps its full 512x288 size");
        var tight = EyeView.Encode(noise, 4000);
        Check(tight != null && tight.Bytes.Length <= 4000 && tight.WidthPx < 512 && tight.HeightPx >= EyeView.MinEdgePx, "a tight cap halves the picture until it fits");
        Check(EyeView.Encode(noise, 10) == null, "nothing is returned when even the smallest picture does not fit");
    }

    private void TestEyeLayers()
    {
        Check((EyeView.GubbleEyeCullMask & EyeView.GubbleBodyLayer) == 0 && (EyeView.GubbleEyeCullMask & EyeView.WorldLayer) != 0,
            "the Gubble's eye draws the world layer and leaves out the Gubble's own body layer");
        EyeView.MarkBody(_companion, EyeView.GubbleBodyLayer);
        var gubble = _companion.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
        var player = _player.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
        Check(gubble.Count > 0 && gubble.All(g => g.Layers == EyeView.GubbleBodyLayer), $"every drawn part of the Gubble's body is on its own layer ({gubble.Count} parts)");
        Check(player.Count > 0 && player.All(g => (g.Layers & EyeView.GubbleEyeCullMask) != 0), "the player's body stays on a layer the Gubble's eye draws");
        Check(_player.EyeCamera.CullMask == EyeView.AllLayers || (_player.EyeCamera.CullMask & EyeView.GubbleBodyLayer) != 0, "the player's own cameras still draw the Gubble");
        Check(EyeView.MarkBody(_companion, EyeView.GubbleBodyLayer) == 0, "marking the body again changes nothing");
    }

    private void TestEyesSetting()
    {
        var root = ProjectSettings.GlobalizePath(TestRoot + "/eyes-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        string Profile(string name, string? text)
        {
            var path = System.IO.Path.Combine(root, name);
            if (text != null) System.IO.File.WriteAllText(path, text);
            return path;
        }
        Check(CommandHost.LoadGubbleEyes(Profile("missing.cfg", null)), "with no profile the setting is on (the founder's default for now)");
        Check(!CommandHost.LoadGubbleEyes(Profile("off.cfg", "[profile]\nversion=1\nai_eyes=false\n")), "the player's profile can turn the eyes off");
        Check(CommandHost.LoadGubbleEyes(Profile("on.cfg", "[profile]\nversion=1\nai_eyes=true\ncompanion_name=\"the Gubble\"\n")), "and on");
        Check(CommandHost.LoadGubbleEyes(Profile("unset.cfg", "[profile]\nversion=1\nplayer_color=2\n")), "a profile that never chose keeps the default");
        Check(!CommandHost.LoadGubbleEyes(Profile("odd.cfg", "[profile]\nversion=1\nai_eyes=\"yes\"\n")), "anything but a yes or no reads as off");
        // A profile that cannot be read (here a folder where the file should be; a parse error reads the same way).
        var unreadable = Profile("unreadable.cfg", null);
        System.IO.Directory.CreateDirectory(unreadable);
        Check(!CommandHost.LoadGubbleEyes(unreadable), "a profile that does not load reads as off");
        System.IO.Directory.Delete(root, true);
    }

    private void TestEyeFacts()
    {
        Check(CommandHost.Heading(Vector3.Forward) == 0 && CommandHost.Heading(Vector3.Right) == 90 && CommandHost.Heading(Vector3.Back) == 180 && CommandHost.Heading(Vector3.Left) == 270,
            "headings run from the room's -Z toward +X");
        Check(CommandHost.Heading(new Vector3(-1e-9f, 0, -1)) == 0, "a heading a hair left of -Z rounds to 0, never 360");
        Check(CommandHost.DayPhase(2, -30) == "night" && CommandHost.DayPhase(6.5f, -2) == "dawn" && CommandHost.DayPhase(8, 8) == "morning"
            && CommandHost.DayPhase(12, 60) == "noon" && CommandHost.DayPhase(15, 40) == "afternoon" && CommandHost.DayPhase(18.5f, 6) == "evening"
            && CommandHost.DayPhase(19.5f, -3) == "dusk", "the time of day is said in a word from the clock and the sun's height");
    }

    private async Task TestLookRefusals()
    {
        _host.SetGubbleEyes(true);
        _now += TimeSpan.FromSeconds(5); // a full bucket of looks
        var asPlayer = await Look(Gubble(actor: CommandHost.PlayerAvatar));
        Check(Code(asPlayer) == "actor_denied" && asPlayer["error"]!["field_path"]!.GetValue<string>() == "$.args.actor"
            && asPlayer["error"]!["message"]!.GetValue<string>() == CommandHost.LookActorMessage, "the companion cannot look through the player's eyes");
        var tooHigh = await Look(Gubble(pitch: 61));
        Check(Code(tooHigh) == "invalid_args" && tooHigh["error"]!["field_path"]!.GetValue<string>() == "$.args.pitch_deg"
            && tooHigh["error"]!["allowed"]!.GetValue<double>() == 60, "the eye tilts at most 60 degrees");
        var tooFar = await Look(Gubble(yaw: -90.5));
        Check(Code(tooFar) == "invalid_args" && tooFar["error"]!["field_path"]!.GetValue<string>() == "$.args.yaw_deg", "and turns at most 90 degrees");
        var odd = await Look(new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["zoom"] = 2 });
        Check(Code(odd) == "field_unknown", "a look takes no other argument");

        _host.SetGubbleEyes(false);
        var off = await Look(Gubble());
        Check(Code(off) == "permission_denied" && off["error"]!["message"]!.GetValue<string>() == CommandHost.EyesOffMessage
            && !off["error"]!["retryable"]!.GetValue<bool>() && off["op"]!.GetValue<string>() == "view.look",
            "with the player's setting off the look is refused, saying so");
        _host.SetGubbleEyes(true);

        // Headless there is no renderer: each look that passes the checks spends one of LookBurst tokens and answers unavailable.
        var answers = new System.Collections.Generic.List<JsonObject>();
        for (var i = 0; i < CommandHost.LookBurst + 1; i++) answers.Add(await Look(Gubble(yaw: 30, pitch: 20)));
        Check(answers.Take(CommandHost.LookBurst).All(a => Code(a) == "unavailable" && a["error"]!["message"]!.GetValue<string>() == CommandHost.NoRendererMessage
            && !a["error"]!["retryable"]!.GetValue<bool>() && a["data"] == null && a["query_id"]!.GetValue<string>().StartsWith("look-", StringComparison.Ordinal)),
            "headless, a look is refused as unavailable, with no data");
        Check(Code(answers[^1]) == "rate_limited" && answers[^1]["error"]!["retryable"]!.GetValue<bool>(), $"at most {CommandHost.LookBurst} looks at once");
        _now += TimeSpan.FromSeconds(CommandHost.LookRefillS);
        Check(Code(await Look(Gubble())) == "unavailable", "a second later the Gubble may look again");
        Check(Code(await Look(Gubble())) == "rate_limited", "one more look comes back each second");
        var player = await Look(Gubble(), Player);
        Check(Code(player) == "unavailable", "the player's own look is neither rate limited nor subject to the AI's setting");
        var sync = Query("view.look", Gubble(), Player);
        Check(Code(sync) == "unavailable", "the synchronous path refuses a look too");
        Check(_host.GetNodeOrNull("GubbleEye") == null, "no eye rig is made while nothing can be drawn");
        Check(Code(Query("observe", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }, Companion)) == null, "observe still answers");
        _now += TimeSpan.FromSeconds(5);
    }
}
