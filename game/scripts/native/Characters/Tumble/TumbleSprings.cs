using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// The spring chains (K1's hair, hats, dangling legs, arms on springs, wands and sparkles) through a tumble: held still from the
/// flop to the end of the get-up, every part riding the hull the ground holds, then reset to the pose they ride in and let go.
/// Measured in the suite: free, a chain lagging 20° behind a body turning at 6 rad/s sweeps a hat's brim or a hand up to 1.7 cm
/// into the ground the body rolls onto, and still 1 to 3 mm while the body rises; a spring collider (a plane on the ground) only
/// keeps the chain's centre line out, never the part's mesh. The hold is the rig's own (<see cref="SecondaryMotion.Held"/>), so
/// the contact layer letting go of the leg IK while the tumble suspends it (which asks for the leg springs) never frees the legs
/// mid-flop (review major 3); let go, each simulator is as active as the rig wants it then.
/// </summary>
public sealed class TumbleSprings
{
    private readonly CharacterBody _body;
    private bool _holding;

    public TumbleSprings(CharacterBody body) => _body = body;

    /// <summary>The springs are held still.</summary>
    public bool Held => _holding;

    /// <summary>Every chain rides its parent as posed.</summary>
    public void Hold()
    {
        if (_holding) return;
        _holding = true;
        _body.Secondary.Held = true;
    }

    /// <summary>The springs start again from the pose they ride in (no jump from where they last were), as active as the rig wants them.</summary>
    public void Release()
    {
        if (!_holding) return;
        _holding = false;
        foreach (var simulator in new[] { _body.Secondary.LegSimulator, _body.Secondary.Simulator })
            if (simulator != null && GodotObject.IsInstanceValid(simulator)) simulator.Reset();
        _body.Secondary.Held = false;
    }
}
