using System;
using Godot;

namespace GarageGame.Native.Characters.Contact;

/// <summary>
/// K4's installer: how a drawn character meets the land (docs/runs/RUN-2-CHARACTERS.md, "Contact"). The integrator calls
/// <see cref="Install"/> once per <see cref="CharacterVisual"/> it puts on a body; nothing else in the game needs to know
/// about these layers.
/// </summary>
public static class CharacterContact
{
    /// <summary>
    /// Adds to the visual's pose stack: the <see cref="ContactLayer"/> (layer 2: feet, slopes, steps, landings and stumbles,
    /// water, climbing, carrying, wind), the <see cref="LookTargetLayer"/> (layer 3: where the face looks) and a guard that lets
    /// go of the leg IK and the look-ats while the tumble suspends those layers. <paramref name="body"/> is the capsule the
    /// visual is worn by (its rays skip it); <paramref name="pointOfInterest"/>, when given, is a world point the face glances at
    /// now and then and watches while it falls (the integrator passes the Gubble for the player and the player for the Gubble).
    /// A second call on the same visual does nothing.
    /// </summary>
    public static void Install(CharacterVisual visual, SmallPlayerController body, Func<Vector3?>? pointOfInterest = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(body);
        if (FindContact(visual) != null) return;
        var contact = new ContactLayer(visual.Body, body);
        var look = new LookTargetLayer(visual.Body, contact, pointOfInterest);
        visual.Stack.Add(contact);
        visual.Stack.Add(look);
        visual.Stack.Add(new ContactGuard(visual.Stack, contact, look));
    }

    /// <summary>The visual's contact layer, or null before <see cref="Install"/>.</summary>
    public static ContactLayer? FindContact(CharacterVisual visual)
    {
        for (var i = 0; i < visual.Stack.Count; i++)
            if (visual.Stack[i] is ContactLayer contact) return contact;
        return null;
    }

    /// <summary>The visual's look-target layer, or null before <see cref="Install"/>.</summary>
    public static LookTargetLayer? FindLook(CharacterVisual visual)
    {
        for (var i = 0; i < visual.Stack.Count; i++)
            if (visual.Stack[i] is LookTargetLayer look) return look;
        return null;
    }
}

/// <summary>
/// Writes no bones. Runs after the layers the tumble (K7) suspends, and while they are suspended lets go of what they hold
/// outside the pose: the leg IK (whose targets would pin the flopping feet) and the look-ats. They take hold again, easing in,
/// when the layers run again.
/// </summary>
internal sealed class ContactGuard : IPoseLayer<BodyState>
{
    private readonly PoseStack<BodyState> _stack;
    private readonly ContactLayer _contact;
    private readonly LookTargetLayer _look;

    public ContactGuard(PoseStack<BodyState> stack, ContactLayer contact, LookTargetLayer look)
    {
        _stack = stack;
        _contact = contact;
        _look = look;
    }

    public PoseLayer Layer => PoseLayer.Gesture;

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        if (_stack.IsSuspended(PoseLayer.Contact)) _contact.Release();
        if (_stack.IsSuspended(PoseLayer.Life)) _look.Release();
    }
}
