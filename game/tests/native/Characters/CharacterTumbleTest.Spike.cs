using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Room;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>
/// The spike the plan asks for before the tumble is built (docs/runs/RUN-2-CHARACTERS.md, "The risk"): Godot's
/// PhysicalBoneSimulator3D with PhysicalBone3Ds under Jolt at this 1 to 4 cm scale. Each character's main parts (body, head,
/// arms, legs, big props; at most 10, by role) become a capsule or sphere sized from the part's mesh bounds, joined to the
/// nearest simulated ancestor by a cone-twist joint with angle limits, on no collision layer (so never touching a neighbour or
/// an avatar capsule) and masked to the world (RoomBuilder.BodyMask), under the game's 3.5 m/s² gravity, tethered by a soft
/// spring to where it stood, and launched as the shipped tumble launches (TumbleLaunch). 200 seeded tumbles per character on
/// flat ground, a 30° slope and a 2 cm step, measured against the plan's three criteria. Run with "-- --spike"; the verdict
/// and its numbers are printed as TUMBLE_SPIKE lines, never failed: the shipped tumble's own suite is what passes or fails.
/// </summary>
public partial class CharacterTumbleTest
{
    private const int SpikeTumbles = 200;
    private const float SpikeWindowS = 1.5f;

    private sealed class SpikeRun
    {
        public required CharacterBody Body;
        public required PhysicalBoneSimulator3D Simulator;
        public required PhysicalBone3D Root;
        public required TumbleProbe Probe;
        public required Vector3 Anchor;
        public required float LandingSpeed;
        public required bool[] Simulated;
        public float Deepest;
        public float DeepestSimulated;
        public float ShapeDeepest;
        public float BodiesMovingS;
        public float Fastest;
        public float LastMovingS;
        public float FarthestRoot;
        public bool NonFinite;
    }

    private async Task RunSpike()
    {
        GD.Print($"TUMBLE_SPIKE jolt penetration_slop={Setting("physics/jolt_physics_3d/simulation/penetration_slop")} speculative_contact_distance={Setting("physics/jolt_physics_3d/simulation/speculative_contact_distance")} " +
                 $"baumgarte={Setting("physics/jolt_physics_3d/simulation/baumgarte_stabilization_factor")} velocity_steps={Setting("physics/jolt_physics_3d/simulation/velocity_steps")} position_steps={Setting("physics/jolt_physics_3d/simulation/position_steps")} default_gravity={Setting("physics/3d/default_gravity")}");
        var lane = 0;
        foreach (var id in PlayerCharacters)
        {
            var definition = Definition(id);
            foreach (var ground in Enum.GetValues<TumbleGround>())
            {
                var origin = new Vector3(200 + 10 * lane++, 0, 200);
                var terrain = TumbleTerrain.Build(this, ground, origin, 5.0f);
                var runs = new List<SpikeRun>();
                var random = new TumbleRandom((uint)(lane * 7919 + 17));
                await Frames(2);
                var boneCount = 0;
                for (var i = 0; i < SpikeTumbles; i++)
                {
                    var spot = SpikeSpot(terrain, i, ref random);
                    var run = SpikeBody(definition, terrain, spot, ref random);
                    boneCount = run.Simulator.GetChildCount();
                    runs.Add(run);
                }
                await Frames(1);
                foreach (var run in runs) run.Simulator.PhysicalBonesStartSimulation();
                // The launch, as the shipped tumble launches: the landing velocity, a small hop and a seeded spin.
                foreach (var run in runs)
                {
                    var launch = TumbleLaunch.FromLanding(new Vector3(random.Range(-0.3f, 0.3f), -run.LandingSpeed, random.Range(-0.3f, 0.3f)), random.Next(), dazed: false);
                    var centre = run.Root.GlobalPosition;
                    foreach (var child in run.Simulator.GetChildren())
                    {
                        if (child is not PhysicalBone3D bone) continue;
                        bone.LinearVelocity = launch.Linear + launch.Angular.Cross(bone.GlobalPosition - centre);
                        bone.AngularVelocity = launch.Angular;
                    }
                }
                var dt = 1f / Engine.PhysicsTicksPerSecond;
                for (var frame = 0; frame * dt < SpikeWindowS; frame++)
                {
                    foreach (var run in runs)
                    {
                        // The tether: a soft spring on the body's own bone toward where it stood (horizontal only).
                        var offset = run.Root.GlobalPosition - run.Anchor;
                        offset.Y = 0;
                        var velocity = run.Root.LinearVelocity with { Y = 0 };
                        run.Root.ApplyCentralImpulse((-TumbleLaunch.TetherStiffness * offset - TumbleLaunch.TetherDamping * velocity) * run.Root.Mass * dt);
                    }
                    await Frames(1);
                    var t = (frame + 1) * dt;
                    foreach (var run in runs)
                    {
                        if (!run.Probe.Finite()) { run.NonFinite = true; continue; }
                        var fastest = run.Probe.FastestJoint(dt);
                        if (frame > 1) run.Fastest = Mathf.Max(run.Fastest, fastest);
                        if (fastest > SettledMps) run.LastMovingS = t;
                        run.Deepest = Mathf.Max(run.Deepest, run.Probe.Penetration(terrain));
                        run.DeepestSimulated = Mathf.Max(run.DeepestSimulated, run.Probe.Penetration(terrain, run.Simulated));
                        foreach (var child in run.Simulator.GetChildren())
                        {
                            if (child is not PhysicalBone3D bone) continue;
                            if (bone.LinearVelocity.Length() + 0.02f * bone.AngularVelocity.Length() > SettledMps) run.BodiesMovingS = t;
                            run.ShapeDeepest = Mathf.Max(run.ShapeDeepest, ShapeDepth(bone, terrain));
                        }
                        var root = run.Root.GlobalPosition - run.Anchor;
                        run.FarthestRoot = Mathf.Max(run.FarthestRoot, new Vector2(root.X, root.Z).Length());
                    }
                }
                var settled = runs.Count(r => !r.NonFinite && r.LastMovingS <= 1.0f);
                var shallow = runs.Count(r => !r.NonFinite && r.Deepest <= 0.002f);
                var slow = runs.Count(r => !r.NonFinite && r.Fastest <= r.LandingSpeed + 1);
                var clean = runs.Count(r => !r.NonFinite && r.LastMovingS <= 1.0f && r.Deepest <= 0.002f && r.Fastest <= r.LandingSpeed + 1);
                GD.Print(
                    Inv($"TUMBLE_SPIKE {id} {ground.ToString().ToLowerInvariant()} bones={boneCount} clean={clean}/{runs.Count} settled<=1s={settled} depth<=2mm={shallow} speed<=landing+1={slow} nan={runs.Count(r => r.NonFinite)} ") +
                    Inv($"settle_p50={Percentile(runs.Select(r => r.LastMovingS), 0.5f):0.00}s p95={Percentile(runs.Select(r => r.LastMovingS), 0.95f):0.00}s max={runs.Max(r => r.LastMovingS):0.00}s ") +
                    Inv($"depth_p95={Percentile(runs.Select(r => r.Deepest), 0.95f) * 1000:0.0}mm max={runs.Max(r => r.Deepest) * 1000:0.0}mm ") +
                    Inv($"| simulated parts only: bodies_settled<=1s={runs.Count(r => r.BodiesMovingS <= 1.0f)} p50={Percentile(runs.Select(r => r.BodiesMovingS), 0.5f):0.00}s p95={Percentile(runs.Select(r => r.BodiesMovingS), 0.95f):0.00}s ") +
                    Inv($"mesh_depth_p95={Percentile(runs.Select(r => r.DeepestSimulated), 0.95f) * 1000:0.0}mm max={runs.Max(r => r.DeepestSimulated) * 1000:0.0}mm shape_depth_p95={Percentile(runs.Select(r => r.ShapeDeepest), 0.95f) * 1000:0.0}mm max={runs.Max(r => r.ShapeDeepest) * 1000:0.0}mm | ") +
                    Inv($"overspeed_max={runs.Max(r => r.Fastest - r.LandingSpeed):0.00}m/s root_max={runs.Max(r => r.FarthestRoot) * 100:0.0}cm"));
                foreach (var run in runs) run.Body.QueueFree();
                terrain.Root.QueueFree();
                await Frames(2);
            }
        }
    }

    /// <summary>How far a physical bone's own capsule or sphere lies inside the ground (its lowest point along the ground's normal).</summary>
    private static float ShapeDepth(PhysicalBone3D bone, TumbleTerrain terrain)
    {
        var deepest = 0f;
        foreach (var child in bone.GetChildren())
        {
            if (child is not CollisionShape3D collision) continue;
            var transform = collision.GlobalTransform;
            switch (collision.Shape)
            {
                case SphereShape3D sphere:
                    deepest = Mathf.Max(deepest, terrain.Depth(transform.Origin - terrain.Normal * sphere.Radius));
                    break;
                case CapsuleShape3D capsule:
                    var half = transform.Basis.Y.Normalized() * (capsule.Height / 2 - capsule.Radius);
                    deepest = Mathf.Max(deepest, terrain.Depth(transform.Origin + half - terrain.Normal * capsule.Radius));
                    deepest = Mathf.Max(deepest, terrain.Depth(transform.Origin - half - terrain.Normal * capsule.Radius));
                    break;
            }
        }
        return deepest;
    }

    private static string Setting(string name) => ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).ToString() : "?";

    private static float Percentile(IEnumerable<float> values, float p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Where tumble i stands: a 14 × 15 grid 0.3 m apart; on the step lane, within 3 cm of a rising edge.</summary>
    private static Vector3 SpikeSpot(TumbleTerrain terrain, int i, ref TumbleRandom random)
    {
        var x = (i % 14 - 6.5f) * TumbleTerrain.StepPitchM;
        var z = (i / 14 - 7f) * 0.3f;
        if (terrain.Kind == TumbleGround.Step) x = Mathf.Round(x / TumbleTerrain.StepPitchM) * TumbleTerrain.StepPitchM + random.Range(-0.03f, 0.03f);
        var world = terrain.Origin + new Vector3(x, 0, z);
        return world with { Y = terrain.SurfaceY(world.X, world.Z) };
    }

    private SpikeRun SpikeBody(CharacterDefinition definition, TumbleTerrain terrain, Vector3 spot, ref TumbleRandom random)
    {
        var body = CharacterRig.Build(definition);
        var yaw = random.Range(-Mathf.Pi, Mathf.Pi);
        body.Transform = new Transform3D(new Basis(Vector3.Up, yaw), spot);
        AddChild(body);
        // Lift the standing body clear of the ground (the slope's uphill side, a step's edge).
        var probe = new TumbleProbe(body);
        var lift = 0f;
        for (var i = 0; i < body.BoneCount; i++)
        {
            var mesh = body.PartMesh(i);
            if (mesh?.Mesh == null) continue;
            var corners = mesh.Mesh.GetAabb();
            for (var c = 0; c < 8; c++)
            {
                var local = corners.Position + new Vector3((c & 1) != 0 ? corners.Size.X : 0, (c & 2) != 0 ? corners.Size.Y : 0, (c & 4) != 0 ? corners.Size.Z : 0);
                var world = body.Transform * (body.Skeleton.GetBoneGlobalRest(i) * (mesh.Transform * local));
                lift = Mathf.Max(lift, terrain.SurfaceY(world.X, world.Z) - world.Y);
            }
        }
        body.Position += Vector3.Up * (lift + 0.001f);
        var simulator = new PhysicalBoneSimulator3D { Name = "SpikeBones", Active = true };
        body.Skeleton.AddChild(simulator);
        PhysicalBone3D? root = null;
        var mass = 0f;
        foreach (var bone in TumbleSpikeBones.Select(body))
        {
            var rig = body.Bones[bone];
            var shape = TumbleSpikeBones.ShapeFor(rig);
            var physical = new PhysicalBone3D
            {
                Name = "Physical_" + rig.Name, CollisionLayer = 0, CollisionMask = RoomBuilder.BodyMask,
                Friction = 0.8f, Bounce = 0, GravityScale = 3.5f / (float)ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble(),
                LinearDamp = 0.5f, AngularDamp = 2.0f, CanSleep = true,
                BodyOffset = shape.BodyOffset,
                JointType = bone == body.BodyBone ? PhysicalBone3D.JointTypeEnum.None : PhysicalBone3D.JointTypeEnum.Cone,
                JointOffset = shape.JointOffset,
            };
            physical.Set("bone_name", rig.Name);
            if (physical.JointType == PhysicalBone3D.JointTypeEnum.Cone)
            {
                physical.Set("joint_constraints/swing_span", bone == body.HeadBone ? 25f : 50f);
                physical.Set("joint_constraints/twist_span", 20f);
                physical.Set("joint_constraints/bias", 0.3f);
                physical.Set("joint_constraints/softness", 0.8f);
                physical.Set("joint_constraints/relaxation", 1.0f);
            }
            physical.AddChild(new CollisionShape3D { Shape = shape.Shape, Transform = shape.ShapeTransform });
            if (bone == body.BodyBone)
            {
                root = physical;
                mass = TumbleSpikeBones.Density * shape.Volume;
            }
            physical.Mass = Mathf.Max(TumbleSpikeBones.Density * shape.Volume, mass / 6);
            simulator.AddChild(physical);
        }
        var anchorRoot = root!;
        var simulated = new bool[body.BoneCount];
        foreach (var bone in TumbleSpikeBones.Select(body)) simulated[bone] = true;
        return new SpikeRun
        {
            Body = body, Simulator = simulator, Root = anchorRoot, Probe = probe, Simulated = simulated,
            Anchor = body.Transform * (body.Skeleton.GetBoneGlobalRest(body.BodyBone) * TumbleSpikeBones.ShapeFor(body.Bones[body.BodyBone]).BodyOffset).Origin,
            LandingSpeed = random.Range(1.9f, 3.4f),
        };
    }
}
