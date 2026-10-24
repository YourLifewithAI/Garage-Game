using Godot;
using System;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The hook for expertise outside Glow's own casts (docs/magic/README.md, M11). Glow's are recorded at its own casts
/// (CommandHostGrowth). The Bubble's carry, boat and lift (packet M9) are events the simulation verifies, and call RecordExpertise.
/// Nothing records Fire yet: its list was replaced by a design a later Fire packet builds (the founder, 15 October). The host
/// verifies every condition itself; a preview, a refused or held command, a replay, a restored effect and anything the AI says
/// earn nothing. The store keeps each once per team (GlowProgress), and an expertise is a reward, never a precondition: when
/// the journal cannot be saved the event goes on and what was learned is simply not kept (TrySaveExpertise).
/// </summary>
public partial class CommandHost
{
    /// <summary>
    /// Records an expertise the simulation itself verified: M9's events call it with the ability and the id (ExpertiseLists.Bubbles
    /// with BubbleCarry, BubbleBoat or BubbleLift), once their condition really happened (the first carry that set the player down
    /// safely, the first step into a boat, the first lift that rose the player). It goes through the same save path as every other
    /// expertise: learned once for the team however often it is called again, saved before it is published, with its pace line and
    /// the pool's maximum following. Returns true only when it was newly learned and saved. An unknown ability or id, one already
    /// learned, or a journal that cannot be saved records nothing and returns false; it never throws, since an event is not a
    /// command to refuse.
    /// </summary>
    internal bool RecordExpertise(string ability, string id)
    {
        GlowProgress candidate;
        try { candidate = GlowProgress.WithExpertise(ability, id); }
        catch (GlowProgressException) { return false; }
        return !ReferenceEquals(candidate, GlowProgress) && TrySaveExpertise(candidate);
    }

    /// <summary>M9's events (Kernel/Bubble/CommandHostBubble.cs): the carry, the boat and the lift learned as Bubbles expertise, whose ids are the store's.</summary>
    partial void OnBubbleLearned(string capability) => RecordExpertise(ExpertiseLists.Bubbles, capability);
}
