namespace GarageGame.Native.Characters.Gaits;

/// <summary>The two layers K3 adds to a body's pose stack.</summary>
public readonly record struct GaitLayers(GaitLayer Gait, LifeLayer Life);

/// <summary>
/// K3's installer (the integrator wires every packet's installer at merge; nothing here edits CharacterVisual or RoomWorld):
/// adds the gait (layer 1) and the life layer (layer 3) to a character's pose stack. The gait follows the rig's motion
/// profile; the life layer's blinks follow a seed from the character's id, so the same frames blink the same way in
/// every run and the player and the Gubble blink apart.
/// </summary>
public static class CharacterGaits
{
    /// <summary>Adds the gait and life layers to <paramref name="visual"/>'s stack (once: a second call changes nothing).</summary>
    public static void Install(CharacterVisual visual) => Install(visual.Stack, SeedFor(visual.Body));

    /// <summary>Adds the layers to any stack of the body's states, with the blink seed given; returns them (the ones already there, if any).</summary>
    public static GaitLayers Install(PoseStack<BodyState> stack, uint seed)
    {
        if (FindGait(stack) is { } gait && FindLife(stack) is { } life) return new GaitLayers(gait, life);
        var rig = new GaitRig(stack.Body);
        var layers = new GaitLayers(FindGait(stack) ?? new GaitLayer(rig), FindLife(stack) ?? new LifeLayer(rig, seed));
        if (FindGait(stack) == null) stack.Add(layers.Gait);
        if (FindLife(stack) == null) stack.Add(layers.Life);
        return layers;
    }

    /// <summary>The installed gait layer, or null (the sound packet subscribes to its events here).</summary>
    public static GaitLayer? FindGait(PoseStack<BodyState> stack)
    {
        for (var i = 0; i < stack.Count; i++)
            if (stack[i] is GaitLayer gait) return gait;
        return null;
    }

    public static LifeLayer? FindLife(PoseStack<BodyState> stack)
    {
        for (var i = 0; i < stack.Count; i++)
            if (stack[i] is LifeLayer life) return life;
        return null;
    }

    /// <summary>The blink seed of a body: a stable hash of its character's id.</summary>
    public static uint SeedFor(CharacterBody body) => GaitMath.Hash(body.CharacterId ?? "");
}
