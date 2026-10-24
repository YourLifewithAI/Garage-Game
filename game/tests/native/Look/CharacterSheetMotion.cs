using Godot;
using GarageGame.Native;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Look;

/// <summary>
/// The character sheets' motion (Run 2, characters round): every tile shows the real pose stack instead of the rest pose. Each tile
/// builds its character as the room does (RoomWorld): <see cref="CharacterVisual.Create(CharacterDefinition, Node?)"/> from the
/// game's own library, with <see cref="CharacterMotion.Install"/> (the gaits and life of K3, the contact and look targets of K4, the
/// tumble of K7) on the capsule it would be drawn on; a floating body is drawn on the companion as GubbleCharacter.Dress draws it
/// (its aura source, its gesture layer, its glow anchors), without taking the companion's own body away. The tile's
/// <see cref="CharacterSheetActor"/> then runs the stack once per physics tick from scripted <see cref="BodyState"/>s until a
/// representative moment. Nothing here reads a character's name: the floating motion kind alone chooses the companion's body (and
/// so the cast for the ninth tile), as a floating body is the only kind that never stands, lands, climbs, swims or tumbles.
/// </summary>
public static class CharacterSheetMotion
{
    public static void RegisterCharacterSheetPoses()
    {
        CharacterSheetPoses.BuildBody = Build;
        foreach (var (tile, _) in CharacterSheetPoses.Tiles) CharacterSheetPoses.Register(tile, Pose);
    }

    private static CharacterSheetPoseStatus Pose(CharacterSheetPoseContext context) =>
        context.Visual.GetNodeOrNull<CharacterSheetActor>(CharacterSheetActor.NodeName) is { } actor ? actor.Step(context) : CharacterSheetPoseStatus.Rest;

    /// <summary>A fresh body for one tile, or null (the bare GLB) when neither the game's library nor the folder's yields the character.</summary>
    private static CharacterSheetBody? Build(CharacterSheetBuild request)
    {
        var world = request.World;
        if (world == null || !GodotObject.IsInstanceValid(world) || !GodotObject.IsInstanceValid(world.Player) || !GodotObject.IsInstanceValid(world.Companion)) return null;
        var definition = Definition(request);
        if (definition == null) return null;
        var floats = definition.Motion == MotionKind.Float;
        var companion = world.Companion;
        SmallPlayerController capsule = floats ? companion : world.Player;
        var visual = CharacterVisual.Create(definition, floats ? companion : null);
        if (floats)
        {
            // GubbleCharacter.Dress, but beside the companion's own body rather than in place of it.
            var gestures = new GubbleGestureLayer(companion, visual.Body);
            visual.Stack.Add(gestures);
            var drawn = new DrawnGubble { Name = GubbleCharacter.NodeName };
            drawn.Initialize(companion, visual, gestures, world.Look);
            visual.Body.AddChild(drawn);
        }
        var actor = new CharacterSheetActor();
        CharacterMotion.Install(visual, capsule, () => actor.Interest);
        actor.Initialize(visual, capsule);
        visual.Body.AddChild(actor);
        return new CharacterSheetBody(visual.Body, visual.Look);
    }

    /// <summary>The game's own copy of the folder's character (res://characters, what the room draws), else the folder's own.</summary>
    private static CharacterDefinition? Definition(CharacterSheetBuild request)
    {
        var folder = System.IO.Path.GetFullPath(request.Folder).TrimEnd('/', '\\');
        var id = System.IO.Path.GetFileName(folder);
        if (request.World.Characters is { } shipped && shipped.TryGet(id, out var definition)) return definition;
        var root = System.IO.Path.GetDirectoryName(folder);
        if (root == null) return null;
        return CharacterLibrary.Load(root).TryGet(id, out definition) ? definition : null;
    }
}
