using System.Collections.Generic;
using Godot;
using GarageGame.Native.Look;

namespace GarageGame.Native.Characters;

/// <summary>
/// A drawn character as a body's visual (IBodyVisual, the characters round's Wave 0 hook): the rig (K1, CharacterBody) under
/// the body's visual root, its look (L8, CharacterLook), and the pose stack, run once per physics tick from the body's
/// BodyState. The integrator's glue between the round's packets: the gaits (K3), contact (K4), the Gubble's gestures (K5) and
/// the tumble (K7) are pose layers added to <see cref="Stack"/>; see docs/characters/RIG-API.md.
/// </summary>
public sealed class CharacterVisual : IBodyVisual
{
    /// <summary>The rig: what the body draws.</summary>
    public CharacterBody Body { get; }
    /// <summary>The pose stack the body's ticks run; layers are added by the packets that own them.</summary>
    public PoseStack<BodyState> Stack { get; }
    /// <summary>What the look did (the surfaces and their roles).</summary>
    public CharacterLookResult Look { get; }
    /// <summary>The state of the last tick (Tick 0 before the first).</summary>
    public BodyState LastState { get; private set; }
    /// <summary>Seconds of ticks run (the pose clock's time).</summary>
    public double TimeS => _timeS;

    public Node3D Root => Body;

    private double _timeS;
    private long _frame;

    private CharacterVisual(CharacterBody body, Node? auraSource)
    {
        Body = body;
        Stack = new PoseStack<BodyState>(body);
        Look = CharacterLook.Apply(body, LookParts(body), auraSource);
    }

    /// <summary>
    /// Build a character from the library, or null when the id is empty, unknown or refused (the body keeps the prototype).
    /// auraSource: the node whose aura_color meta tints a bubble's rim and the effects (the Gubble's body); null for none.
    /// </summary>
    public static CharacterVisual? Create(CharacterLibrary? library, string? id, Node? auraSource = null)
    {
        if (library == null || string.IsNullOrEmpty(id) || !library.TryGet(id, out var definition)) return null;
        return new CharacterVisual(CharacterRig.Build(definition), auraSource);
    }

    /// <summary>Build from a definition already in hand (tests).</summary>
    public static CharacterVisual Create(CharacterDefinition definition, Node? auraSource = null) =>
        new(CharacterRig.Build(definition), auraSource);

    /// <summary>The look's view of the rig's parts: names, kinds, effect hints, bubble hints and the rig's roles.</summary>
    public static IReadOnlyList<CharacterLookPart> LookParts(CharacterBody body)
    {
        var parts = new List<CharacterLookPart>(body.BoneCount);
        foreach (var bone in body.Bones)
        {
            if (bone.Part < 0) continue;
            var hint = bone.Bubble is { } bubble ? new CharacterMaterialHint("bubble", SeeThrough: true, Rim: "shimmer", AuraColour: bubble.AuraColour) : null;
            var effect = bone.Effect == EffectMotion.None ? null : bone.Effect.ToString().ToLowerInvariant();
            parts.Add(new CharacterLookPart(bone.Name, bone.Kind == PartKind.Effect ? "effect" : "solid", effect, hint, PartRoles.Token(bone.Role)));
        }
        return parts;
    }

    /// <summary>One physics tick: the stack poses the rig from the body's state.</summary>
    public void BodyTicked(in BodyState state, float delta)
    {
        LastState = state;
        _timeS += delta;
        Stack.Run(state, new PoseClock(_timeS, delta, ++_frame));
    }
}
