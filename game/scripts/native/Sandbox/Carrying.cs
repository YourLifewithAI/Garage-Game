using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Sandbox;

/// <summary>
/// Moves carried things with their holders every physics tick, after the bodies have moved (a higher physics priority),
/// so a carried thing never trails its holder by a tick. The command host owns who holds what; this only places nodes.
/// </summary>
public partial class Carrying : Node
{
    /// <summary>The node of each carried thing and where it rides now, supplied by the command host.</summary>
    public Func<IEnumerable<(Node3D Node, Transform3D Pose)>>? Carried { get; set; }

    public override void _Ready() => ProcessPhysicsPriority = 50;

    public override void _PhysicsProcess(double delta)
    {
        if (Carried == null) return;
        foreach (var (node, pose) in Carried()) Place(node, pose);
    }

    /// <summary>
    /// Put a room object's node at a pose, and tell the physics server at once: a node's transform reaches the server
    /// only when the scene tree flushes its transform notifications, and the next command's queries must see it now.
    /// </summary>
    public static void Place(Node3D node, Transform3D pose)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        node.GlobalTransform = pose;
        if (node is CollisionObject3D body && node.IsInsideTree())
            PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, pose);
    }
}
