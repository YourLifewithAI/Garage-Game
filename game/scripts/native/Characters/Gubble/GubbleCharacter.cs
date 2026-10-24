using Godot;
using GarageGame.Native.Look;

namespace GarageGame.Native.Characters;

/// <summary>
/// The companion's drawn body: the family's Gubble for every player (decided, docs/runs/RUN-2-CHARACTERS.md, "The Gubble"; the
/// character choice never touches it). Dress builds the library's <see cref="CharacterChoice.GubbleId"/> character as a
/// <see cref="CharacterVisual"/> with the companion as its aura source (its aura_color meta tints the bubble's rim and the
/// sparkles), adds the gesture layer to its pose stack, hangs the glow's anchors on the aura bone and the torch flame and names them
/// to the look through the companion's metas, and draws the companion with it in place of the prototype figure (which stays under
/// the visual root, hidden, for the tests and the look). A library without the Gubble leaves the prototype as it is.
/// </summary>
public static class GubbleCharacter
{
    public const string Id = CharacterChoice.GubbleId;
    /// <summary>The name of the <see cref="DrawnGubble"/> node inside the drawn body.</summary>
    public const string NodeName = "Gubble";

    /// <summary>
    /// Draw the companion as the Gubble from the library (the look, when given, supplies the bubble's light level), replacing and
    /// freeing a Gubble drawn before. Returns what it drew, or null when the library has no Gubble (the body is left as it was).
    /// </summary>
    public static DrawnGubble? Dress(CompanionAvatar companion, CharacterLibrary? library, LookDirector? look)
    {
        if (companion == null || !GodotObject.IsInstanceValid(companion)) return null;
        var visual = CharacterVisual.Create(library, Id, companion);
        if (visual == null)
        {
            GD.Print($"CHARACTERS: no {Id} in the library; the companion keeps the prototype figure");
            return null;
        }
        var gestures = new GubbleGestureLayer(companion, visual.Body);
        visual.Stack.Add(gestures);
        var drawn = new DrawnGubble { Name = NodeName };
        drawn.Initialize(companion, visual, gestures, look);
        visual.Body.AddChild(drawn);
        var previous = Of(companion);
        companion.SetBodyVisual(visual);
        if (previous != null && GodotObject.IsInstanceValid(previous.Body)) previous.Body.QueueFree();
        companion.SetMeta(GlowLook.HaloAnchorMeta, drawn.HaloAnchor);
        if (drawn.LightAnchor != null) companion.SetMeta(GlowLook.LightAnchorMeta, drawn.LightAnchor);
        else if (companion.HasMeta(GlowLook.LightAnchorMeta)) companion.RemoveMeta(GlowLook.LightAnchorMeta);
        return drawn;
    }

    /// <summary>The Gubble drawn on the companion now, or null while it wears the prototype (or anything else).</summary>
    public static DrawnGubble? Of(CompanionAvatar companion) =>
        companion != null && GodotObject.IsInstanceValid(companion) && companion.BodyVisual is CharacterVisual visual && GodotObject.IsInstanceValid(visual.Body)
            ? visual.Body.GetNodeOrNull<DrawnGubble>(NodeName) : null;

    /// <summary>Back to the prototype figure: the drawn Gubble is freed and the look's anchors are taken away.</summary>
    public static void Undress(CompanionAvatar companion)
    {
        var drawn = Of(companion);
        if (drawn == null) return;
        companion.SetBodyVisual(null);
        foreach (var meta in new[] { GlowLook.HaloAnchorMeta, GlowLook.LightAnchorMeta })
            if (companion.HasMeta(meta)) companion.RemoveMeta(meta);
        drawn.Body.QueueFree();
    }
}
