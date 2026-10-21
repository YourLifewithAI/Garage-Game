using System;
using System.Linq;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// What the player can ask the Gubble without words (the magic design's "Directing the Gubble without words"; RUN-2-GLOW.md
/// section 4): the six ability slots, fixed by category, the wheel's nine fixed wedges (Fire's the ninth, decision 77), and the smart ask's rules. One
/// icon set serves the wheel, the number keys and the thought bubble, so each teaches the others. The icons and words are a
/// first pass for the founder's eye, kept here together to tune.
/// </summary>
public static class GubbleMagic
{
    /// <summary>The ability slots by category, fixed by the engine: slot 1 light, 2 growth, 3 float, 4 burst, 5 build, 6 fire (decision 77). Keys 1 to 6 are these.</summary>
    public static readonly string[] SlotCategories = { "light", "growth", "float", "burst", "build", "fire" };
    /// <summary>A slot's name while the island has no ability in its category (the island's own display_name replaces it).</summary>
    public static readonly string[] SlotNames = { "Glow", "Grow", "Bubbles", "Fireworks", "Build", "Fire" };
    public static readonly string[] SlotIcons = { "☀", "✿", BubbleIcon, "✸", "⌂", FireIcon };
    /// <summary>Fire's slot (0-based): key 6 and the ninth wedge, just after Fireworks (decision 77).</summary>
    public const int FireSlot = 5;
    /// <summary>Fire's own icon: the slot's, the wheel's wedge and the thought bubble's.</summary>
    public const string FireIcon = "♨";
    /// <summary>Fire's wedge and key until the first rest (decision 77): the reason under the lock.</summary>
    public const string FireLockedWords = "Rest by the starting campfire first.";
    /// <summary>The Bubbles' icon: the slot's, the wheel's wedge, and the thought bubble while the Gubble leads a bubble.</summary>
    public const string BubbleIcon = "○";

    // The orders' icons, and the thought bubble's own.
    public const string FollowIcon = "→";
    public const string StayIcon = "‖";
    public const string ComeIcon = "↩";
    public const string StopIcon = "■";
    public const string LookIcon = "◎";
    public const string FetchIcon = "✋";
    /// <summary>Put down what the Gubble holds (the wheel's Fetch wedge while it holds something, and the smart ask).</summary>
    public const string PutDownIcon = "↓";
    /// <summary>The Fetch wedge's name while the Gubble holds something.</summary>
    public const string PutDownName = "Put down";
    /// <summary>A put-down asked of a Gubble holding nothing: the HUD's own words (no command was sent).</summary>
    public const string NothingHeld = "holding nothing";
    public const string DoneMark = "✓";
    public const string RefusedMark = "✕";
    public const string UnknownIcon = "?";
    /// <summary>What the Gubble says, with a shrug, of a slot the island has no ability for.</summary>
    public const string NotYet = "not yet";
    /// <summary>A fetch asked of nothing: the HUD's own words (no command was sent).</summary>
    public const string NothingToFetch = "aim at something to fetch";
    /// <summary>A tap aimed at nothing (the sky): the HUD's own words.</summary>
    public const string NothingThere = "aim at a spot";
    /// <summary>A walk that could not arrive (the host fails it after its unreachable timeout): the HUD's own words.</summary>
    public const string CannotGetThere = "can't get there";
    /// <summary>The dusk moment's hint: {0} is slot 1's key, {1} the ask button's.</summary>
    public const string DuskHint = "{0} or {1}: Light!";

    public enum WedgeKind { Ability, Come, Stay, Fetch }

    /// <summary>One wedge of the wheel: an ability slot (0-based Slot), or an order.</summary>
    public readonly record struct Wedge(string Id, WedgeKind Kind, int Slot, string Icon, string Name);

    /// <summary>
    /// The wheel's nine wedges, clockwise from the top, never reflowed (muscle memory needs them fixed): Glow up, Fireworks, Fire (the
    /// ninth, just after Fireworks: decision 77), Stay, Grow, Build, Fetch, Come and Bubbles; the other seven keep their clockwise order.
    /// </summary>
    public static readonly Wedge[] Wedges =
    {
        new("glow", WedgeKind.Ability, 0, SlotIcons[0], SlotNames[0]),
        new("fireworks", WedgeKind.Ability, 3, SlotIcons[3], SlotNames[3]),
        new("fire", WedgeKind.Ability, FireSlot, SlotIcons[FireSlot], SlotNames[FireSlot]),
        new("stay", WedgeKind.Stay, -1, StayIcon, "Stay"),
        new("grow", WedgeKind.Ability, 1, SlotIcons[1], SlotNames[1]),
        new("build", WedgeKind.Ability, 4, SlotIcons[4], SlotNames[4]),
        new("fetch", WedgeKind.Fetch, -1, FetchIcon, "Fetch"),
        new("come", WedgeKind.Come, -1, ComeIcon, "Come"),
        new("bubbles", WedgeKind.Ability, 2, SlotIcons[2], SlotNames[2]),
    };

    /// <summary>The wedge a pointer offset from the wheel's centre falls in (screen pixels, +Y down), or null inside centrePx: the centre cancels.</summary>
    public static int? WedgeAt(Vector2 offset, float centrePx)
    {
        if (!offset.IsFinite() || offset.Length() < centrePx) return null;
        // 0 is straight up, increasing clockwise; each wedge is a ninth of the turn, centred on its direction.
        var angle = Mathf.Atan2(offset.X, -offset.Y);
        return Mathf.PosMod(Mathf.RoundToInt(angle / WedgeTurn), Wedges.Length);
    }

    /// <summary>One wedge's share of the turn, in radians (a ninth).</summary>
    public static float WedgeTurn => Mathf.Tau / Wedges.Length;

    /// <summary>The direction of a wedge's middle from the wheel's centre (screen space, +Y down): 0 straight up, clockwise.</summary>
    public static Vector2 WedgeDirection(int index) => new(Mathf.Sin(index * WedgeTurn), -Mathf.Cos(index * WedgeTurn));

    /// <summary>The wedge of an ability slot (0-based).</summary>
    public static int WedgeOfSlot(int slot) => Array.FindIndex(Wedges, w => w.Kind == WedgeKind.Ability && w.Slot == slot);

    /// <summary>
    /// The smart ask's rules, the first that matches wins. While the Gubble holds something: put it down here, or put it there.
    /// SelectBubble, LeadBubble and ClearBubble are the Bubble's steering (decision 57): the first right-click on a bubble selects it,
    /// the next on a spot leads it there, and a right-click on nothing (or on the bubble again, or on the Gubble) lets it go.
    /// </summary>
    public enum AskRule { None, Toggle, Fetch, Glow, Look, PutHere, PutThere, SelectBubble, LeadBubble, ClearBubble }

    /// <summary>
    /// What the aim is on: whether it hit anything, where, the room entity it hit (an id, never a name), and whether it is the Gubble.
    /// Bubble is the bubble's effect id when the aim passes through it before anything else (the kernel's BubbleAt picks it), and
    /// InBubble that the view is inside it (a first-person view in a carry): every ray from there is "on" the bubble.
    /// </summary>
    public readonly record struct Aim(bool Hit, Vector3 Point, string? Entity, bool AtGubble, string? Bubble = null, bool InBubble = false);

    /// <summary>What a tap would ask, at what point and of what thing.</summary>
    public readonly record struct Ask(AskRule Rule, Aim Aim)
    {
        public Vector3 Point => Aim.Point;
        public string? Target => Aim.Entity;
    }

    /// <summary>
    /// The smart ask (the first rule that matches wins): aimed at the Gubble, switch stay and follow; at a carryable thing, fetch it;
    /// at a dark spot within Glow's reach, glow there; otherwise go and look there. Nothing hit: nothing to ask. While the Gubble
    /// holds something (the founder's playtest: it could pick a thing up but not put it down), aimed at the Gubble it puts it down
    /// here, and aimed at a spot it puts it there.
    /// </summary>
    public static Ask Decide(Aim aim, Func<string, bool> carryable, Func<Vector3, bool> darkWithinReach, bool gubbleHolds = false, string? selectedBubble = null)
    {
        // A bubble is selected: the next ask is its steering, whatever else the aim is on. A spot leads it there; nothing, the Gubble
        // or the bubble itself lets it go (the view inside the bubble is not "on" it: from there every ray is a spot).
        if (selectedBubble != null)
            return !aim.Hit || aim.AtGubble || (aim.Bubble == selectedBubble && !aim.InBubble) ? new(AskRule.ClearBubble, aim) : new(AskRule.LeadBubble, aim);
        if (aim.Bubble != null) return new(AskRule.SelectBubble, aim);
        if (gubbleHolds && aim.AtGubble) return new(AskRule.PutHere, aim);
        if (gubbleHolds) return new(aim.Hit ? AskRule.PutThere : AskRule.None, aim);
        if (aim.AtGubble) return new(AskRule.Toggle, aim);
        if (aim.Entity != null && carryable(aim.Entity)) return new(AskRule.Fetch, aim);
        if (!aim.Hit) return new(AskRule.None, aim);
        if (darkWithinReach(aim.Point)) return new(AskRule.Glow, aim);
        return new(AskRule.Look, aim);
    }

    /// <summary>The reticle's words for what a tap would do now (icons and fixed words only, never a thing's name).</summary>
    public static string AskWords(AskRule rule, bool following, string glowName) => rule switch
    {
        AskRule.SelectBubble => $"{BubbleIcon} choose this bubble",
        AskRule.LeadBubble => $"{BubbleIcon} lead it there",
        AskRule.ClearBubble => $"{BubbleIcon} let the bubble go",
        AskRule.Toggle => following ? $"{StayIcon} wait here" : $"{FollowIcon} follow me",
        AskRule.Fetch => $"{FetchIcon} fetch it",
        AskRule.Glow => $"{SlotIcons[0]} {glowName.ToLowerInvariant()} there",
        AskRule.Look => $"{LookIcon} go and look",
        AskRule.PutHere => $"{PutDownIcon} put it down here",
        AskRule.PutThere => $"{PutDownIcon} put it there",
        _ => "",
    };

    /// <summary>
    /// The host's reason, short, beside the head-shake: its first clause, at most 60 characters. The host's refusals are its own
    /// templates (never a name or the player's words), so the text is the host's.
    /// </summary>
    public static string ShortReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "can't";
        var text = message.Trim();
        var cut = text.IndexOfAny(new[] { '.', ';', '!', '?' });
        if (cut > 0) text = text[..cut];
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 60 ? text : text[..59].TrimEnd() + "…";
    }
}
