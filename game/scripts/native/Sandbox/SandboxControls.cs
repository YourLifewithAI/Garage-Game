using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Sandbox;

/// <summary>
/// What the player's hand keys aim at (F pick up and put down, V push). The keys only choose a target and send the
/// same garage_game.command the companion would; the host's checks decide. Aim is the body's facing, which follows the
/// mouse in F1 and F2 and the direction of travel in F3 and F4, so the keys work the same in every view.
/// </summary>
public static class SandboxControls
{
    /// <summary>A thing counts as in front within this angle of the body's facing.</summary>
    public const float AheadDegrees = 70f;

    /// <summary>
    /// The movable object the body faces within reach (reaches: near enough, with a clear way to it), for picking up or
    /// pushing: one within the limit first (so the book beside the doorstop does not block it), then the nearest, so a key
    /// on something too heavy gets the host's reason.
    /// </summary>
    public static JsonObject? ThingAhead(SmallPlayerController body, IReadOnlyList<JsonObject> entities, float limitKg, Func<string, float> massOf, Func<JsonObject, bool> reaches) =>
        entities
            .Where(e => Kind(e) == "object" && e["movable"]!.GetValue<bool>() && e["held_by"] == null && !StandsOn(body, Box(e)))
            .Select(e => (Entity: e, Box: Box(e)))
            .Where(c => Angle(body, c.Box) <= AheadDegrees && reaches(c.Entity))
            .OrderBy(c => massOf(Id(c.Entity)) <= limitKg ? 0 : 1)
            .ThenBy(c => Gap(body, c.Box))
            .ThenBy(c => Id(c.Entity), StringComparer.Ordinal)
            .Select(c => c.Entity).FirstOrDefault();

    /// <summary>The thing with a walkable top the body faces within reach, above the ground it stands on: where F sets a carried thing.</summary>
    public static JsonObject? SupportAhead(SmallPlayerController body, IReadOnlyList<JsonObject> entities, string heldId, Func<JsonObject, bool> reaches) =>
        entities
            .Where(e => Kind(e) == "object" && Id(e) != heldId && e["held_by"] == null &&
                e["affordances"]!.AsArray().Any(a => a!.GetValue<string>() == "walkable_top") && !StandsOn(body, Box(e)))
            .Select(e => (Entity: e, Box: Box(e)))
            .Where(c => c.Box.End.Y > body.GlobalPosition.Y + 0.01f && Angle(body, c.Box) <= AheadDegrees && reaches(c.Entity))
            .OrderBy(c => Gap(body, c.Box)).ThenBy(c => Id(c.Entity), StringComparer.Ordinal)
            .Select(c => c.Entity).FirstOrDefault();

    /// <summary>The spot on a support's top nearest the body that a thing of this footprint fits, kept 5 mm in from the edges.</summary>
    public static Vector3 SpotOn(SmallPlayerController body, Aabb support, Aabb footprint)
    {
        var feet = body.GlobalPosition;
        float Axis(float value, float low, float high, float centre) => low <= high ? Mathf.Clamp(value, low, high) : centre;
        var x = Axis(feet.X, support.Position.X - footprint.Position.X + 0.005f, support.End.X - footprint.End.X - 0.005f, support.GetCenter().X);
        var z = Axis(feet.Z, support.Position.Z - footprint.Position.Z + 0.005f, support.End.Z - footprint.End.Z - 0.005f, support.GetCenter().Z);
        return new Vector3(x, support.End.Y, z);
    }

    /// <summary>
    /// The focus tag's words and place (RUN-2-OPEN-SEA.md, "Things to touch"), kept together for the founder to tune by eye.
    /// The keys they name are the input map's now (PlayerControls.Label), so a remapped hand key shows in the tag at once.
    /// </summary>
    public static class FocusWords
    {
        public static string PickUpOrPush => $"{PlayerControls.Label(Act.Hands)} pick up · {PlayerControls.Label(Act.Push)} push";
        public static string PushOnly => $"{PlayerControls.Label(Act.Push)} push · too heavy to lift";
        public const string TooHeavy = "too heavy to move";
        /// <summary>A format: {0} is the carried thing's name and {1} the thing it would be set on (a key's name is escaped for the format).</summary>
        public static string SetOn => PlayerControls.Label(Act.Hands).Replace("{", "{{").Replace("}", "}}") + " set {0} on {1}";
        /// <summary>The tag sits this far right of and above the thing's top, in pixels.</summary>
        public const float TagRightPx = 14f;
        public const float TagUpPx = 6f;
        /// <summary>The top-right corner kept free for the minimap, in pixels; a tag that would fall there moves below it.</summary>
        public const float MinimapWidthPx = 300f;
        public const float MinimapHeightPx = 240f;
    }

    /// <summary>The body's facing on the floor plane.</summary>
    public static Vector3 Facing(SmallPlayerController body)
    {
        var facing = -body.GlobalBasis.Z;
        facing.Y = 0;
        return facing.LengthSquared() > 1e-6f ? facing.Normalized() : Vector3.Forward;
    }

    /// <summary>Whether the body stands on top of this box (the rug under your feet is not something in front of you).</summary>
    public static bool StandsOn(SmallPlayerController body, Aabb box)
    {
        var feet = body.GlobalPosition;
        return feet.X >= box.Position.X && feet.X <= box.End.X && feet.Z >= box.Position.Z && feet.Z <= box.End.Z &&
            feet.Y >= box.End.Y - 0.003f && feet.Y <= box.End.Y + 0.01f;
    }

    private static float Angle(SmallPlayerController body, Aabb box)
    {
        var feet = body.GlobalPosition;
        var nearest = new Vector3(Mathf.Clamp(feet.X, box.Position.X, box.End.X), feet.Y, Mathf.Clamp(feet.Z, box.Position.Z, box.End.Z));
        var toward = nearest - feet;
        // Over or under it: aim at its middle instead.
        if (toward.LengthSquared() < 1e-6f) toward = new Vector3(box.GetCenter().X - feet.X, 0, box.GetCenter().Z - feet.Z);
        if (toward.LengthSquared() < 1e-6f) return 0;
        return Mathf.RadToDeg(Facing(body).AngleTo(toward.Normalized()));
    }

    private static float Gap(SmallPlayerController body, Aabb box)
    {
        var feet = body.GlobalPosition;
        return new Vector2(feet.X, feet.Z).DistanceTo(new Vector2(Mathf.Clamp(feet.X, box.Position.X, box.End.X), Mathf.Clamp(feet.Z, box.Position.Z, box.End.Z)));
    }

    private static string Id(JsonObject entity) => entity["id"]!.GetValue<string>();

    private static string Kind(JsonObject entity) => entity["kind"]!.GetValue<string>();

    public static Aabb Box(JsonObject entity)
    {
        var low = Read(entity["bounds_m"]!["min_m"]!);
        var high = Read(entity["bounds_m"]!["max_m"]!);
        return new Aabb(low, high - low);
    }

    private static Vector3 Read(JsonNode node) => new((float)node[0]!.GetValue<double>(), (float)node[1]!.GetValue<double>(), (float)node[2]!.GetValue<double>());
}
