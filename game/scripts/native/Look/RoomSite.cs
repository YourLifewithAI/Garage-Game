using Godot;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Native.Look;

/// <summary>
/// Where a room stands, as far as the sun is concerned: the latitude, which way the room's -Z axis faces, and the clock
/// hour of mean solar noon. These are facts about a room, not about a style, so they belong to the room manifest's
/// optional `site` key (proposed in docs/look/proposals/room-site.md), never to the shared style preset (review M5).
/// They are deliberately coarse: a whole degree of latitude and a quarter hour of solar noon say which sun a room
/// gets, and nothing about where it is. A longitude, a place or a finer latitude is refused, so a manifest can never
/// carry one through here.
/// </summary>
public sealed record RoomSite(float LatitudeDeg, float NegZBearingDeg, float SolarNoonH, string Source)
{
    public const string FromRoom = "room";
    public const string FromFallback = "fallback";

    /// <summary>
    /// The site of a room that declares none: 30 degrees north, -Z facing north, mean solar noon at 12:00 standard
    /// time. Rooms should say where they stand; the look warns whenever it has to use this.
    /// </summary>
    public static readonly RoomSite Fallback = new(30f, 0f, 12f, FromFallback);

    /// <summary>True when the room's own manifest supplied the site.</summary>
    public bool Declared => Source == FromRoom;

    /// <summary>The keys the manifest's site object may hold. Anything else, a longitude above all, is refused.</summary>
    public static readonly string[] Keys = { "latitude_deg", "neg_z_bearing_deg", "solar_noon_h" };

    /// <summary>Hemisphere of the seasons, from the sign of the latitude.</summary>
    public string Hemisphere => LatitudeDeg < 0f ? "south" : "north";

    /// <summary>
    /// Reads the manifest's `site` object from its bytes. Null when the manifest has none. Throws
    /// InvalidOperationException, naming the key, for a site this look must not accept: unknown keys (a longitude, a
    /// place), a latitude that is not a whole number of degrees within 66 degrees of the equator, a bearing outside
    /// 0 to 360 or not whole degrees, a solar noon outside 10:00 to 14:00 or not on a quarter hour.
    /// </summary>
    public static RoomSite? FromManifest(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("site", out var site)) return null;
        if (site.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("site must be an object");
        foreach (var property in site.EnumerateObject())
            if (!Keys.Contains(property.Name))
                throw new InvalidOperationException($"site.{property.Name} is not allowed: a site holds only {string.Join(", ", Keys)} (never a longitude or a place)");
        float Number(string name)
        {
            if (!site.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) throw new InvalidOperationException($"site.{name} is required and must be a number");
            var number = value.GetSingle();
            return float.IsFinite(number) ? number : throw new InvalidOperationException($"site.{name} must be finite");
        }
        var latitude = Number("latitude_deg");
        var bearing = Number("neg_z_bearing_deg");
        var noon = Number("solar_noon_h");
        if (latitude is < -66f or > 66f || latitude != MathF.Round(latitude))
            throw new InvalidOperationException("site.latitude_deg must be a whole number of degrees within 66 degrees of the equator");
        if (bearing is < 0f or >= 360f || bearing != MathF.Round(bearing))
            throw new InvalidOperationException("site.neg_z_bearing_deg must be a whole number of degrees from 0 up to (not including) 360");
        if (noon is < 10f or > 14f || noon * 4f != MathF.Round(noon * 4f))
            throw new InvalidOperationException("site.solar_noon_h must be between 10 and 14 and on a quarter hour");
        return new RoomSite(latitude, bearing, noon, FromRoom);
    }

    /// <summary>
    /// The site of a loaded room, read from its manifest on disk (RoomData does not carry it yet; the day it does, this
    /// reader goes away). The file is read again only if it still has the hash RoomData verified, so what the look reads
    /// is what the room loaded. A room without a site, or with one this look refuses, gets the fallback and a warning.
    /// </summary>
    public static RoomSite For(RoomData room, out string warning)
    {
        warning = "";
        var path = room.Directory + "/room.json";
        try
        {
            if (!FileAccess.FileExists(path)) { warning = $"room {room.RoomId} has no manifest on disk to read a site from"; return Fallback; }
            var bytes = FileAccess.GetFileAsBytes(path);
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != room.ManifestSha256)
            {
                warning = $"room {room.RoomId}'s manifest changed since it was loaded, so its site was not read";
                return Fallback;
            }
            var site = FromManifest(bytes);
            if (site != null) return site;
            warning = $"room {room.RoomId} declares no site: the sun uses the look's fallback ({Fallback.LatitudeDeg:0} degrees north, -Z facing {Fallback.NegZBearingDeg:0}, solar noon {Fallback.SolarNoonH:0.##} h)";
            return Fallback;
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException)
        {
            warning = $"room {room.RoomId}'s site is not usable ({error.Message}); the sun uses the look's fallback";
            return Fallback;
        }
    }
}
