using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Mark 1 of the first recorded play (16 October), the arm's part: F2's arm hangs from the body's right shoulder, 2.2 radii out from its
/// middle, so a wall or a face on the body's right met it within centimetres (or held the shoulder itself) and the camera stood beside the
/// body, which left the frame. Now a camera held in comes in behind the body: in the real room, with a wall along the body's right at
/// three distances and the eye at three pitches, the body's middle stays in the shoulder camera's frame, the camera stays outside the body
/// and on the body's side of the wall; with no wall the arm is today's (TestShoulderNearEndUnchanged).
/// </summary>
public partial class PlayHudTest
{
    private async Task TestShoulderKeepsTheBodyInFrame()
    {
        var player = _world.Player;
        var follow = player.GetNode<SpringArm3D>("FollowCameraArm").GetNode<Camera3D>("FollowCamera");
        var results = new List<string>();
        var allIn = true;
        foreach (var faceM in new[] { 0.025f, 0.048f, 0.07f })
            foreach (var pitch in new[] { -0.4f, 0f, 0.5f })
            {
                await StandForViews(FreeSpot, 0.4f, pitch);
                // A wall along the body's right, its face faceM from the body's middle line (0.025: the shoulder inside it; 0.048: the
                // shoulder's lens against it; 0.07: a little room), 1.2 m long and 0.4 m tall.
                var right = player.GlobalBasis.X;
                var wall = new StaticBody3D { Name = "ArmTestWall", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
                wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.1f, 0.4f, 1.2f) } });
                _world.AddChild(wall);
                wall.GlobalTransform = new Transform3D(player.GlobalBasis, player.GlobalPosition + right * (faceM + 0.05f) + Vector3.Up * 0.2f);
                await Frames(8);
                var middle = PlayerMiddle();
                var inFrame = follow.Current && follow.IsPositionInFrustum(middle) && !follow.IsPositionBehind(middle);
                var lowAxis = player.GlobalPosition + Vector3.Up * player.BodyRadiusM;
                var highAxis = player.GlobalPosition + Vector3.Up * (player.BodyHeightM - player.BodyRadiusM);
                var axis = highAxis - lowAxis;
                var along = Mathf.Clamp((follow.GlobalPosition - lowAxis).Dot(axis) / axis.LengthSquared(), 0, 1);
                var toAxis = follow.GlobalPosition.DistanceTo(lowAxis + axis * along);
                var side = (follow.GlobalPosition - player.GlobalPosition).Dot(right);
                var ok = inFrame && toAxis >= player.BodyRadiusM && side < faceM;
                allIn &= ok;
                results.Add(FormattableString.Invariant($"face {faceM * 100:0.0} cm, pitch {pitch:0.0}: {(inFrame ? "in frame" : "OUT")}, {toAxis * 100:0.0} cm from the axis, {side * 100:0.0} cm to the right"));
                wall.QueueFree();
                await Frames(2);
            }
        GD.Print("PLAY_HUD_MEASURED shoulder arm beside a wall: " + string.Join("; ", results));
        Check(allIn, "with a wall along the body's right the shoulder camera keeps the body's middle in its frame, stays outside the body and on its side of the wall: " + string.Join("; ", results));
        await StandForViews(FreeSpot, 0.4f, 0f);
        Check(follow.GetParent<SpringArm3D>().Position == new Vector3(player.BodyRadiusM * 2.2f, player.BodyHeightM * 0.8f, 0),
            "with the wall gone the arm hangs from the shoulder again");
    }
}
