using System;
using Godot;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Native.Characters;

/// <summary>
/// Every packet's pose layers on a drawn body, in one place (the integrator's; docs/runs/RUN-2-CHARACTERS.md): the gaits and
/// the life layer (K3), contact with the land and the look targets (K4), the tumble (K7). The Gubble's gesture layer (K5) is
/// added by GubbleCharacter.Dress. Each installer adds its layers once; the stack orders them by PoseLayer.
/// </summary>
public static class CharacterMotion
{
    /// <summary>
    /// Bring a drawn body to life. body: the capsule it draws (its state and its control hold). pointOfInterest: where the face
    /// glances now and then (the Gubble for the player, the player for the Gubble); null for none.
    /// </summary>
    public static void Install(CharacterVisual visual, SmallPlayerController body, Func<Vector3?>? pointOfInterest = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(body);
        CharacterGaits.Install(visual);
        CharacterContact.Install(visual, body, pointOfInterest);
        // A floating body (the Gubble) never falls, so it never tumbles.
        if (!body.Floats) TumbleInstaller.Install(visual, body);
    }
}
