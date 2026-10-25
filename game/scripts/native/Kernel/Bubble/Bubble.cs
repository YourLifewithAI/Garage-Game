using Godot;
using System;

namespace GarageGame.Native.Kernel;

/// <summary>
/// What the place made of the Bubble (decision 56, as Glow's variants): on the player a carry round them (Target names them), on
/// water a boat, anywhere else a bubble that waits for the player to walk into it (a carry with no target).
/// </summary>
public enum BubbleKind { Carry, Boat }

/// <summary>
/// How a bubble ended (decision 110, the Gubble's pop defined): the player's dive key, X, effect.stop or wish.stop; its timer; the Gubble's
/// own choice (its goal.stop, its effect.stop, a new goal of its own ending its lead, its recast, its runner ending the wish whose lead it
/// was; decision 96: a fall that hurts after it is the Gubble's consequence); or anything else, the world's (a rules change, the body put
/// down elsewhere or gone, the room unloading, the player's new order or recast, a wish's action cap), which never leaves a consequence.
/// The receipt's ended words are the contract's: the Gubble's own pop is "popped", as anything else.
/// </summary>
public enum BubbleEnd { PoppedByPlayer, TimedOut, PoppedByGubble, Popped }

/// <summary>
/// One bubble in the room: transient, like an effect (it lives for the session and nothing about it is saved), held by the command
/// host and drawn by the look. The kernel decides whether it holds the player; the body rides it (SmallPlayerController.Bubble.cs).
/// One at a time (decision 59).
/// </summary>
internal sealed class Bubble
{
    /// <summary>'effect:' and 26 random base32 characters: effect.stop names it, goal.stop of the Gubble pops it, and goal.set lead leads it.</summary>
    public required string Id { get; init; }
    /// <summary>The expertise its price and receipt name: bubble_carry (on the player, or a bubble to walk into) or bubble_boat.</summary>
    public required string Capability { get; init; }
    public required BubbleKind Kind { get; init; }
    /// <summary>The op that cast it: effect.start (the Bubble, the place deciding) or nature.cast (an expertise named).</summary>
    public required string Op { get; init; }
    /// <summary>Who cast it: the player stops any bubble, a companion only its own.</summary>
    public required string Principal { get; init; }
    public required string ActionId { get; init; }
    /// <summary>The cast's fingerprint, so a later update writes to this cast's receipt and no other.</summary>
    public required string Fingerprint { get; init; }
    /// <summary>The avatar a carry was cast on (the ghost first), or null for one at a spot (the walk-in) and a boat.</summary>
    public string? Target { get; init; }
    public required float Radius { get; init; }
    public required DateTime Expires { get; init; }
    /// <summary>The rules pack it was cast under: it pops when the rules change, as effects end.</summary>
    public IslandRules? Rules { get; init; }
    /// <summary>The bubble's centre (a carry's sphere; the boat's deck, at its middle).</summary>
    public Vector3 Center { get; set; }
    /// <summary>Its motion over the last tick, which the body riding it is carried by (m/s).</summary>
    public Vector3 Velocity { get; set; }
    /// <summary>The height it holds when nothing raises it: where it formed, where it began to hold the player, or the top of a lift.</summary>
    public float BaseY { get; set; }
    /// <summary>It holds the player now: enveloped (a carry) or aboard (the boat).</summary>
    public bool Holding { get; set; }
    /// <summary>It has held the player: from the cast for one on the player, from the walk-in, from the first step aboard.</summary>
    public bool Carried { get; set; }
    /// <summary>The player left the boat inside its rim (the dive key): it takes them again only once they have stepped off it.</summary>
    public bool BoardLatch { get; set; }
    /// <summary>How long the body that it holds has been outside it.</summary>
    public double RiderLostS { get; set; }
    /// <summary>The Gubble's lead (goal.set lead) taking it to a spot, or null: a bubble moves only while it is led.</summary>
    public BubbleLead? Lead { get; set; }
    /// <summary>The lead that last moved it while it held the player (kept after that lead ended): it carried them where they are.</summary>
    public BubbleLead? CarriedBy { get; set; }

    /// <summary>The receipt's word for how it ended.</summary>
    public static string EndWord(BubbleEnd end) => end switch
    {
        BubbleEnd.PoppedByPlayer => "popped_by_player",
        BubbleEnd.TimedOut => "timed_out",
        _ => "popped",
    };

    /// <summary>The effect.start receipt's word for what the place made of it.</summary>
    public string KindWord => Kind == BubbleKind.Boat ? "boat" : Target != null ? "carry" : "walk_in";
}

/// <summary>
/// The Gubble leading a bubble to a spot (goal.set lead): whose order it was (the lift's price follows it), its job, the spot and how
/// high the bubble rests there, and the lift's progress. Goals stay free and nothing about one is saved.
/// </summary>
internal sealed class BubbleLead
{
    public required string JobId { get; init; }
    /// <summary>Who set the lead: the player's order or the Gubble's own idea. A lift is charged to them when it starts to rise.</summary>
    public required string Principal { get; init; }
    public required string ActionId { get; init; }
    public required string Fingerprint { get; init; }
    /// <summary>The spot (position_m) and the height of the ground (or a boat's water) under it, which the bubble rests over.</summary>
    public required Vector3 Spot { get; init; }
    public required float SpotGroundY { get; init; }
    /// <summary>The spot is higher than the bubble can drift (BubbleLaws.LiftAboveDriftM): it floats up there, a lift.</summary>
    public required bool Lift { get; init; }
    /// <summary>The lift has begun to rise (charged then, once), and the height it rises to (its rest height, at most LiftRiseM up).</summary>
    public bool Rising { get; set; }
    public float RiseToY { get; set; }
    /// <summary>How long it has wanted to move toward the spot and could not (or, rising, to rise and could not).</summary>
    public double BlockedS { get; set; }
    /// <summary>It carried the player into fire that hurt them, and the journal has that consequence fact (one per lead).</summary>
    public bool HarmNoted { get; set; }
}
