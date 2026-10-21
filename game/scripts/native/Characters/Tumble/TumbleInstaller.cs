using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// Puts the tumble on a drawn body (K7): the integrator calls <see cref="Install"/> wherever the character visual is made
/// (RoomWorld), beside the gaits', contact's and the Gubble's installers. Nothing else needs to know the tumble is there.
/// </summary>
public static class TumbleInstaller
{
    /// <summary>The tumbles installed on each body, so the knockout delay can be taken back when none is left on it.</summary>
    private static readonly ConditionalWeakTable<SmallPlayerController, List<CharacterTumble>> Installed = new();

    /// <summary>
    /// Adds the tumble to <paramref name="visual"/>'s pose stack (its last layer) and sets the body's
    /// <see cref="SmallPlayerController.KnockoutRecoveryDelayS"/> to the knockout tumble's length, so the host's recovery at the
    /// spawn waits for the tumble in Challenge and Live. Idempotent. A floating body (the Gubble) gets the layer but never
    /// tumbles and never delays a recovery. When the visual leaves the tree its tumble ends and frees its physics body; when no
    /// tumbling visual is left on the body the delay goes back to 0 (the prototype figure has no tumble to wait for).
    /// </summary>
    public static void Install(CharacterVisual visual, SmallPlayerController body)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(body);
        if (CharacterTumble.Find(visual) != null) return;
        var tumble = new CharacterTumble(visual, body);
        visual.Stack.Add(tumble);
        var installed = Installed.GetOrCreateValue(body);
        installed.RemoveAll(other => !GodotObject.IsInstanceValid(other.Visual.Body));
        installed.Add(tumble);
        Delay(body);
        var root = visual.Body;
        root.TreeEntered += () => Delay(body);
        root.TreeExiting += () =>
        {
            tumble.Abort();
            if (!GodotObject.IsInstanceValid(body)) return;
            foreach (var other in installed)
                if (other != tumble && GodotObject.IsInstanceValid(other.Visual.Body) && other.Visual.Body.IsInsideTree()) return;
            body.KnockoutRecoveryDelayS = 0;
        };
    }

    private static void Delay(SmallPlayerController body)
    {
        if (GodotObject.IsInstanceValid(body) && !body.Floats) body.KnockoutRecoveryDelayS = TumbleTiers.KnockoutSeconds;
    }
}
