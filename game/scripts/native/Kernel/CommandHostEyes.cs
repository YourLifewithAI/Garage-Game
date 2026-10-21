using Godot;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Look;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The Gubble's eyes for the connected AI (view.look; docs/companion/PERCEPTION.md, "The Gubble's eyes"): one small picture
/// drawn now from the Gubble's eye in the shared world, never the player's screen, with plain facts (where it stands, which
/// way it faces, the island's time of day). The eye can turn and tilt within bounds without moving the body, to look up a
/// cliff. The boundary (docs/companion/SECURITY.md):
/// - only the Gubble's own eyes: the actor is the companion's avatar for every principal, never the player's;
/// - a camera shows only its line of sight, so nothing behind a wall is drawn; the picture carries no path, URL or save data;
/// - the player's own setting, "Let my AI see through the Gubble's eyes" (the profile's ai_eyes, default on), turns it off
///   with permission_denied; nothing a request carries changes it;
/// - at most LookBurst looks at once, refilled one every LookRefillS (rate_limited), and LookImageMaxBytes per picture;
/// - headless, with no renderer, the answer is unavailable;
/// - a look is a companion request like any other: its sight fills the team's map exactly as every query does, no more.
/// Drawing takes a frame or two, so the bridge asks through <see cref="HandleObjectAsync"/>; the synchronous path refuses.
/// </summary>
public partial class CommandHost
{
    /// <summary>Looks a companion may take at once; one more comes back every LookRefillS.</summary>
    public const int LookBurst = 3;
    public const double LookRefillS = 1.0;
    /// <summary>The encoded picture's hard cap (96 KiB; base64 131,072 characters), well under the 256 KiB result.</summary>
    public const int LookImageMaxBytes = 98304;
    public const int LookWidthPx = 512;
    public const int LookHeightPx = 288;
    /// <summary>How far the eye may turn left or right of the body's facing, and tilt up or down (degrees).</summary>
    public const double LookMaxYawDeg = 90;
    public const double LookMaxPitchDeg = 60;
    /// <summary>The player's profile key (RoomHud.ProfilePath, [profile] section) for "Let my AI see through the Gubble's eyes".</summary>
    public const string EyesProfileKey = "ai_eyes";
    /// <summary>The setting's value when the player never chose (the founder, 13 October: on for now).</summary>
    public const bool DefaultGubbleEyes = true;

    public const string EyesOffMessage = "The player has turned off \"Let my AI see through the Gubble's eyes\". Use observe instead.";
    public const string NoRendererMessage = "This game runs without a renderer (headless), so the Gubble cannot take a picture. Use observe instead.";
    public const string NotDrawnMessage = "The game did not draw the picture in time (its window may be minimized). Look again in a moment.";
    public const string LookRateMessage = "The Gubble looks about once a second; wait a moment and look again.";
    public const string LookActorMessage = "Only the Gubble's own eyes can be looked through.";

    private bool? _gubbleEyes;
    private double _lookTokens = LookBurst;
    private DateTime _lookTokensAt = DateTime.MinValue;
    private PendingLook? _pendingLook;
    private bool _lookCanWait;
    private EyeView? _eye;

    private sealed record PendingLook(Transform3D Eye, float VerticalFovDeg, float NearM, float FarM);

    /// <summary>
    /// A look succeeded: its picture is in the answer, which goes out now. Carries the principal who looked. The HUD's eye icon
    /// blinks when it was the player's AI (nothing shows on the Gubble itself: the founder, 13 October).
    /// </summary>
    public event Action<string>? PictureTaken;

    /// <summary>Test seam: draws the picture from the eye's pose in place of the renderer, so a headless suite can see a look succeed.</summary>
    internal Func<Transform3D, Task<EyePicture?>>? LookCaptureForTests { get; set; }

    /// <summary>
    /// The player's setting "Let my AI see through the Gubble's eyes": read once from the player's profile (never room state),
    /// then whatever the player's own control sets this session. A profile that exists but does not load counts as off.
    /// </summary>
    public bool GubbleEyesAllowed => _gubbleEyes ??= LoadGubbleEyes(RoomHud.ProfilePath);

    /// <summary>The player's control (the HUD's setting) and tests. No command reaches this.</summary>
    public void SetGubbleEyes(bool allowed) => _gubbleEyes = allowed;

    public static bool LoadGubbleEyes(string profilePath)
    {
        var config = new ConfigFile();
        var loaded = config.Load(profilePath);
        if (loaded == Error.FileNotFound) return DefaultGubbleEyes;
        if (loaded != Error.Ok) return false;
        var value = config.GetValue("profile", EyesProfileKey, DefaultGubbleEyes);
        return value.VariantType == Variant.Type.Bool && value.AsBool();
    }

    /// <summary>
    /// The bridge's entry point: as <see cref="HandleObject"/>, but a view.look that passed every check waits a frame or two for
    /// its picture, on the main thread, without blocking it. Everything else answers at once.
    /// </summary>
    public Task<JsonObject> HandleObjectAsync(string message, string principal)
    {
        _pendingLook = null;
        _lookCanWait = true;
        JsonObject result;
        try { result = HandleObject(message, principal); }
        finally { _lookCanWait = false; }
        var look = _pendingLook;
        _pendingLook = null;
        return look == null || result["ok"]?.GetValue<bool>() != true ? Task.FromResult(result) : FinishLook(result, look);
    }

    private static void CheckLookArgs(JsonElement args)
    {
        if (!args.TryGetProperty("actor", out var actor) || actor.ValueKind != JsonValueKind.String || !AvatarId.IsMatch(actor.GetString()!))
            throw new Refusal("request_invalid", "actor is an avatar id.", "$.args.actor");
        foreach (var (name, limit) in new[] { ("yaw_deg", LookMaxYawDeg), ("pitch_deg", LookMaxPitchDeg) })
        {
            if (!args.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number)
                throw new Refusal("invalid_args", "A value has the wrong type.", "$.args." + name);
            var number = value.GetDouble();
            if (number > limit) throw new Refusal("invalid_args", "A value is above the allowed maximum.", "$.args." + name, actual: number, allowed: limit);
            if (number < -limit) throw new Refusal("invalid_args", "A value is below the allowed minimum.", "$.args." + name, actual: number, allowed: -limit);
        }
    }

    /// <summary>view.look's checks, in order: the actor, the player's setting, the rate, the renderer. The facts come back now; the picture follows.</summary>
    private JsonObject ViewLook(JsonElement args, string principal)
    {
        if (Str(args, "actor") != CompanionAvatarId) throw new Refusal("actor_denied", LookActorMessage, "$.args.actor");
        if (Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree()) throw new Refusal("not_ready", "The Gubble is not in the room.", retryable: true);
        var companion = principal == CompanionPrincipal;
        if (companion && !GubbleEyesAllowed) throw new Refusal("permission_denied", EyesOffMessage);
        if (companion && !TakeLookToken()) throw new Refusal("rate_limited", LookRateMessage, retryable: true);
        if (!EyeView.CanRender && LookCaptureForTests == null) throw new Refusal("unavailable", NoRendererMessage);
        if (!_lookCanWait) throw new Refusal("unavailable", "A picture takes a frame to draw; ask over the companion link.");

        var yawDeg = args.TryGetProperty("yaw_deg", out var yaw) ? yaw.GetDouble() : 0;
        var pitchDeg = args.TryGetProperty("pitch_deg", out var pitch) ? pitch.GetDouble() : 0;
        var eyeCamera = Companion.EyeCamera;
        var eye = eyeCamera.GlobalPosition;
        var forward = -Companion.GlobalBasis.Z;
        var basis = EyeView.LookBasis(forward, yawDeg, pitchDeg);
        var aspect = (double)LookWidthPx / LookHeightPx;
        var heading = Heading(forward);
        var data = new JsonObject
        {
            ["actor"] = CompanionAvatarId,
            ["position_m"] = KernelJson.Vector(Companion.GlobalPosition),
            ["eye_m"] = KernelJson.Vector(eye),
            ["heading_deg"] = heading,
            ["view_heading_deg"] = Round1(PositiveDegrees(heading + yawDeg)),
            ["view_pitch_deg"] = Round1(pitchDeg),
            ["fov_deg"] = Round1(2 * Math.Atan(Math.Tan(Mathf.DegToRad(eyeCamera.Fov) / 2.0) * aspect) * 180 / Math.PI),
        };
        if (LookHook != null && IsInstanceValid(LookHook) && LookHook.Moment is { } moment)
        {
            var hour = Math.Round(PositiveDegrees(moment.Hour * 15.0) / 15.0, 1, MidpointRounding.AwayFromZero);
            data["time_of_day"] = new JsonObject { ["hour"] = hour >= 24 ? 0 : hour, ["phase"] = DayPhase(moment.Hour, moment.SunElevationDeg) };
        }
        _pendingLook = new PendingLook(new Transform3D(basis, eye), eyeCamera.Fov, eyeCamera.Near, eyeCamera.Far);
        return data;
    }

    private async Task<JsonObject> FinishLook(JsonObject result, PendingLook look)
    {
        var principal = result["principal"]!.GetValue<string>();
        var queryId = result["query_id"]?.GetValue<string>();
        EyePicture? picture = null;
        try
        {
            if (Companion != null && IsInstanceValid(Companion)) EyeView.MarkBody(Companion, EyeView.GubbleBodyLayer);
            picture = LookCaptureForTests != null ? await LookCaptureForTests(look.Eye)
                : await EyeRig().Capture(look.Eye, look.VerticalFovDeg, EyeView.GubbleEyeCullMask, new Vector2I(LookWidthPx, LookHeightPx), look.NearM, look.FarM, LookImageMaxBytes);
        }
        catch (Exception error) { GD.Print("COMMAND_HOST look failed: " + error.GetType().Name); }
        if (picture == null || picture.Bytes.Length > LookImageMaxBytes)
            return Fail("view.look", principal, null, queryId, new Refusal("unavailable", NotDrawnMessage, retryable: true));
        result["data"]!["image"] = new JsonObject
        {
            ["media_type"] = picture.MediaType, ["width_px"] = picture.WidthPx, ["height_px"] = picture.HeightPx,
            ["bytes"] = picture.Bytes.Length, ["data_base64"] = Convert.ToBase64String(picture.Bytes), ["untrusted"] = true,
        };
        if (CanonicalJson.Bytes(result).Length > MaxResultBytes)
            return Fail("view.look", principal, null, queryId, new Refusal("internal_error", "The answer would be larger than 256 KiB."));
        PictureTaken?.Invoke(principal);
        return result;
    }

    private EyeView EyeRig()
    {
        if (_eye == null || !IsInstanceValid(_eye))
        {
            _eye = new EyeView { Name = "GubbleEye" };
            AddChild(_eye);
        }
        return _eye;
    }

    /// <summary>A token bucket on the host's clock: LookBurst at most, one more every LookRefillS.</summary>
    private bool TakeLookToken()
    {
        var now = Clock();
        if (_lookTokensAt != DateTime.MinValue)
            _lookTokens = Math.Min(LookBurst, _lookTokens + Math.Max(0, (now - _lookTokensAt).TotalSeconds) / LookRefillS);
        _lookTokensAt = now;
        if (_lookTokens < 1) return false;
        _lookTokens -= 1;
        return true;
    }

    /// <summary>A facing on the floor as a heading: degrees from the room's -Z turning toward +X (0 is -Z, 90 is +X), 0 to 360.</summary>
    public static double Heading(Vector3 forward) =>
        forward.X * forward.X + forward.Z * forward.Z < 1e-12 ? 0 : Round1(PositiveDegrees(Math.Atan2(forward.X, -forward.Z) * 180 / Math.PI));

    /// <summary>The island's light in a word, from the clock and the sun's height (degrees above the horizon).</summary>
    public static string DayPhase(float hour, float sunElevationDeg) =>
        sunElevationDeg <= -6 ? "night"
        : sunElevationDeg < 3 ? (hour < 12 ? "dawn" : "dusk")
        : sunElevationDeg < 12 ? (hour < 12 ? "morning" : "evening")
        : hour < 11 ? "morning" : hour < 13 ? "noon" : "afternoon";

    private static double PositiveDegrees(double degrees)
    {
        var value = degrees % 360;
        if (value < 0) value += 360;
        return value;
    }

    private static double Round1(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded >= 360 ? 0 : rounded == 0 ? 0 : rounded;
    }
}
