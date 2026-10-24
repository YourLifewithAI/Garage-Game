using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Characters;

/// <summary>Control held and released (early by moving, within the bound), the knockout, the hazard entry point, the capsule, the cost.</summary>
public partial class CharacterTumbleTest
{
    private sealed class Watch
    {
        public required Wearer Wearer;
        public required bool Move;
        public float Height;
        public double StartS = -1;
        public double ReleasedS = -1;
        public double EndS = -1;
        public bool SuspendedInFlop = true;
        public bool SawFlop;
        public bool SawGetUp;
        public bool ResumedInGetUp = true;
        public float HeldDrift;
        public Vector3 HeldFrom;
        public bool MovedAfter;
        public bool StarsShown;
        public bool StarsNearHead = true;
        public float RootTurned;
        public bool SpringsHeld = true;
    }

    /// <summary>Every spring of the body still: both simulators inactive (review major 3).</summary>
    private static bool SpringsStill(CharacterBody rig) =>
        (rig.Secondary.LegSimulator is not { } legs || !legs.Active) && (rig.Secondary.Simulator is not { } others || !others.Active);

    /// <summary>After a tumble the leg springs run exactly while the leg IK does not plant the feet.</summary>
    private static bool LegsAsTheIkWants(CharacterBody rig) =>
        (rig.Secondary.LegSimulator is not { } legs || legs.Active == !(rig.LegSolver?.Active ?? false)) &&
        (rig.Secondary.Simulator is not { } others || others.Active);

    private async Task<List<Watch>> WatchFalls(Vector3 origin, (string Id, float Height, bool Move)[] falls, Action<SmallPlayerController>? prepare = null, int frames = 260)
    {
        var watches = new List<Watch>();
        for (var i = 0; i < falls.Length; i++)
            watches.Add(new Watch { Wearer = Wear(falls[i].Id, origin + new Vector3(0.6f * i, falls[i].Height, 0)), Move = falls[i].Move, Height = falls[i].Height });
        await Frames(1);
        foreach (var watch in watches)
        {
            watch.Wearer.Body.SetWorldPhysics(Preset("room_tuned", watch.Wearer.Body.WorldPhysicsRevision + 1));
            prepare?.Invoke(watch.Wearer.Body);
        }
        for (var frame = 0; frame < frames; frame++)
        {
            await Frames(1);
            foreach (var watch in watches)
            {
                var body = watch.Wearer.Body;
                var tumble = watch.Wearer.Tumble;
                var now = watch.Wearer.Visual.TimeS;
                if (watch.StartS < 0 && tumble.IsTumbling)
                {
                    watch.StartS = now;
                    watch.HeldFrom = body.GlobalPosition;
                    if (watch.Move) body.SetControlInput(Vector2.Up);
                }
                if (watch.StartS < 0) continue;
                var stack = watch.Wearer.Visual.Stack;
                if (tumble.IsTumbling) watch.SpringsHeld &= SpringsStill(watch.Wearer.Visual.Body);
                if (tumble.Phase == TumblePhase.Flop)
                {
                    watch.SawFlop = true;
                    watch.SuspendedInFlop &= stack.IsSuspended(PoseLayer.Gait) && stack.IsSuspended(PoseLayer.Contact) && stack.IsSuspended(PoseLayer.Life);
                    watch.RootTurned = Mathf.Max(watch.RootTurned, watch.Wearer.Visual.Body.Pose.GetRotation(CharacterBody.RootBone).GetAngle());
                }
                if (tumble.Phase == TumblePhase.GetUp)
                {
                    watch.SawGetUp = true;
                    watch.ResumedInGetUp &= !stack.IsSuspended(PoseLayer.Gait) && !stack.IsSuspended(PoseLayer.Contact) && !stack.IsSuspended(PoseLayer.Life);
                }
                if (tumble.Dazed && tumble.Stars is { Visible: true } stars)
                {
                    watch.StarsShown = true;
                    var head = watch.Wearer.Visual.Body.Attachment(watch.Wearer.Visual.Body.HeadBone).GlobalPosition;
                    for (var i = 0; i < TumbleStars.Count; i++)
                        watch.StarsNearHead &= stars.StarPosition(i).IsFinite() && stars.StarPosition(i).DistanceTo(head) < 0.08f;
                }
                if (body.ControlHeld)
                {
                    var drift = body.GlobalPosition - watch.HeldFrom;
                    watch.HeldDrift = Mathf.Max(watch.HeldDrift, new Vector2(drift.X, drift.Z).Length());
                }
                else if (watch.ReleasedS < 0) watch.ReleasedS = now;
                if (watch.EndS < 0 && !tumble.IsTumbling) watch.EndS = now;
                if (watch.ReleasedS >= 0 && watch.Move && (body.GlobalPosition - watch.HeldFrom).Length() > 0.01f) watch.MovedAfter = true;
            }
        }
        return watches;
    }

    private async Task TestControl()
    {
        var origin = new Vector3(80, 0, 0);
        var floor = Floor(origin + new Vector3(1.5f, 0, 0), 6);
        await Frames(1);
        var falls = new List<(string, float, bool)>();
        foreach (var id in PlayerCharacters)
        {
            falls.Add((id, 0.9f, false));
            falls.Add((id, 0.9f, true));
        }
        var watches = await WatchFalls(origin, falls.ToArray());
        foreach (var watch in watches)
        {
            var name = $"{watch.Wearer.Visual.Body.CharacterId} {(watch.Move ? "moving" : "still")}";
            var length = watch.Wearer.Tumble.LastLengthS;
            Check(watch.StartS >= 0 && watch.SawFlop && watch.SawGetUp && watch.EndS > 0, $"{name}: a 0.9 m fall tumbles, flops and gets up");
            Check(watch.SuspendedInFlop && watch.ResumedInGetUp, $"{name}: Gait, Contact and Life wait while it flops and pose the standing body again for the get-up");
            Check(watch.SpringsHeld && LegsAsTheIkWants(watch.Wearer.Visual.Body),
                $"{name}: every spring is held still from the flop to the end of the get-up, though contact lets go of the leg IK meanwhile, then runs as the leg IK wants (review major 3)");
            Check(watch.RootTurned > 0.3f, Inv($"{name}: physics turns the root bone (by up to {Mathf.RadToDeg(watch.RootTurned):0} degrees)"));
            if (!watch.Move)
                Check(Mathf.Abs(length - TumbleTiers.TumbleSeconds) <= 1.5f * Dt && Mathf.Abs((float)(watch.ReleasedS - watch.StartS) - TumbleTiers.TumbleSeconds) <= 2.5f * Dt &&
                      watch.Wearer.Tumble.EarlyGetUps == 0,
                    Inv($"{name}: control is held for the whole {TumbleTiers.TumbleSeconds} s tumble (tumbled {length:0.000} s, released after {watch.ReleasedS - watch.StartS:0.000} s)"));
            else
                Check(watch.Wearer.Tumble.EarlyGetUps == 1 && length < TumbleTiers.TumbleSeconds - 0.1f && length >= TumbleTiers.TumbleSeconds - TumbleTiers.EarlyGetUpSeconds - 1.5f * Dt &&
                      Mathf.Abs((float)(watch.ReleasedS - watch.StartS) - length) <= 2.5f * Dt,
                    Inv($"{name}: moving starts the get-up early, at most {TumbleTiers.EarlyGetUpSeconds} s sooner, then control is released (tumbled {length:0.000} s, released after {watch.ReleasedS - watch.StartS:0.000} s)"));
            Check(watch.HeldDrift < 0.002f, Inv($"{name}: while control is held the capsule takes no input (drifted {watch.HeldDrift * 1000:0.0} mm)"));
            if (watch.Move) Check(watch.MovedAfter, $"{name}: released, the body walks off");
            // The layers below pose the standing body (the gait's breath and lean, contact's feet on the floor): the get-up hands the
            // root back to them upright where the capsule stands, and the tumble leaves the pose alone.
            var rig = watch.Wearer.Visual.Body;
            var pose = rig.Pose;
            var turned = pose.GetRotation(CharacterBody.RootBone).GetAngle();
            turned = Mathf.Min(turned, Mathf.Tau - turned);
            var away = pose.GetPosition(CharacterBody.RootBone).DistanceTo(rig.Bones[0].RestLocal);
            // (A body that walks off leans into its walk: only one that stays is upright.)
            Check(watch.Wearer.Tumble.Phase == TumblePhase.Idle && (watch.Move || turned < 0.05f && away < 0.006f) && pose.NonFiniteWrites == 0 && !watch.Wearer.Tumble.Hull.Active,
                Inv($"{name}: the get-up blends back to the standing pose (root {Mathf.RadToDeg(turned):0.0}° from upright, {away * 1000:0.0} mm from its rest) and the tumbling body is gone"));
        }
        foreach (var watch in watches) watch.Wearer.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }

    private async Task TestKnockout()
    {
        var origin = new Vector3(90, 0, 0);
        var floor = Floor(origin + new Vector3(1.5f, 0, 0), 6);
        await Frames(1);
        var falls = PlayerCharacters.Select(id => (id, 1.9f, true)).ToArray();
        var watches = await WatchFalls(origin, falls, frames: 220);
        foreach (var watch in watches)
        {
            var name = watch.Wearer.Visual.Body.CharacterId;
            var length = watch.Wearer.Tumble.LastLengthS;
            Check(watch.StartS >= 0 && watch.Wearer.Tumble.LastTier == TumbleTier.Knockout && Mathf.Abs(length - TumbleTiers.KnockoutSeconds) <= 1.5f * Dt &&
                  watch.Wearer.Tumble.EarlyGetUps == 0 && Mathf.Abs((float)(watch.ReleasedS - watch.StartS) - TumbleTiers.KnockoutSeconds) <= 2.5f * Dt,
                Inv($"{name}: a knockout tumbles for {TumbleTiers.KnockoutSeconds} s with control held throughout, moving or not (tumbled {length:0.000} s)"));
            Check(watch.StarsShown && watch.StarsNearHead && watch.Wearer.Tumble.Stars is { Visible: false } stars && stars.GetChildCount() == TumbleStars.Count &&
                  stars.GetChildren().OfType<MeshInstance3D>().All(m => m.Layers == watch.Wearer.Visual.Body.PartMesh(watch.Wearer.Visual.Body.BodyBone)!.Layers &&
                      m.MaterialOverride is StandardMaterial3D { EmissionEnabled: true }),
                $"{name}: dazed stars circle the head for the knockout's length, emissive, on the body's own layers (so never in its own eye), then go");
        }
        foreach (var watch in watches) watch.Wearer.Body.QueueFree();
        // A host that holds the body longer (Challenge's knockout recovery for a player already hurt) gets the knockout's tumble.
        var hurt = await WatchFalls(origin, new[] { ("potato_man", 0.9f, false) }, body =>
        {
            body.FallObserved = (speed, deep) =>
            {
                if (PlayerVitals.FallDamage(speed, deep) > 0) body.HoldControl(body.KnockoutRecoveryDelayS);
            };
        }, frames: 220);
        var tumble = hurt[0].Wearer.Tumble;
        Check(tumble.LastTier == TumbleTier.Tumble && hurt[0].StarsShown && Mathf.Abs(tumble.LastLengthS - TumbleTiers.KnockoutSeconds) <= 1.5f * Dt,
            Inv($"a tumble-tier fall that the host turns into a knockout (its recovery waits KnockoutRecoveryDelayS) tumbles dazed for the knockout's length ({tumble.LastLengthS:0.000} s)"));
        hurt[0].Wearer.Body.QueueFree();
        // Review minor 4: a fall below the tumble tier that knocks the player out (a stumble's cost from their last health) is the
        // knockout's tumble too: the host's recovery waits for it, so the body never stands frozen with control held and no tumble.
        var stumbled = await WatchFalls(origin, new[] { ("tomato_man", 0.42f, false) }, body =>
        {
            body.FallObserved = (speed, deep) =>
            {
                if (PlayerVitals.FallDamage(speed, deep) > 0) body.HoldControl(body.KnockoutRecoveryDelayS);
            };
        }, frames: 220);
        var knocked = stumbled[0].Wearer.Tumble;
        Check(knocked.LastTier == TumbleTier.Stumble && knocked.Tumbles == 1 && knocked.EarlyGetUps == 0 && stumbled[0].StarsShown &&
              Mathf.Abs(knocked.LastLengthS - TumbleTiers.KnockoutSeconds) <= 1.5f * Dt && !stumbled[0].Wearer.Body.ControlHeld,
            Inv($"a stumble-tier fall that knocks the player out tumbles dazed for the knockout's length while the recovery waits ({knocked.LastTier}, {knocked.LastLengthS:0.000} s)"));
        stumbled[0].Wearer.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }

    private async Task TestHazardEntryPoint()
    {
        var origin = new Vector3(100, 0, 0);
        var floor = Floor(origin);
        var wearer = Wear("stickbear", origin + new Vector3(0, 0.003f, 0));
        await Frames(10);
        var tumble = wearer.Tumble;
        Check(!tumble.Tumble(new Vector3(float.NaN, 0, 0), 1) && !tumble.Tumble(Vector3.Right, -1) && !tumble.Tumble(Vector3.Right, float.PositiveInfinity) && !wearer.Body.ControlHeld,
            "Tumble refuses a non-finite push or a length that is not finite and positive, and holds nothing then");
        Check(tumble.Tumble(new Vector3(0.6f, 0, 0), 1.5f) && wearer.Body.ControlHeld && Mathf.IsEqualApprox(wearer.Body.ControlHoldS, 1.5f),
            "Tumble(impulse, seconds) knocks a standing body over and holds control for its length at once");
        await Frames(2);
        Check(tumble.IsTumbling && !tumble.Dazed && Mathf.IsEqualApprox(tumble.Seconds, 1.5f) && tumble.Hull.Active, "the hazard's tumble flops on the physics body, not dazed below a knockout's length");
        var again = tumble.Tumble(new Vector3(-0.4f, 0, 0), 2.5f);
        await Frames(2);
        Check(again && tumble.Dazed && tumble.Seconds > 2.5f && tumble.Tumbles == 1, Inv($"a second blow while tumbling pushes again and tumbles longer, dazed past a knockout's length ({tumble.Seconds:0.00} s in all)"));
        await Frames(160);
        Check(!tumble.IsTumbling && !wearer.Body.ControlHeld && tumble.Tumbles == 1, Inv($"and it ends, control back ({tumble.LastLengthS:0.00} s in all)"));
        Check(tumble.Tumble(Vector3.Up, 60) && Mathf.IsEqualApprox(wearer.Body.ControlHoldS, SmallPlayerController.MaxControlHoldS), "a very long tumble is held for at most MaxControlHoldS");
        await Frames(2);
        // Taken off mid-tumble (the character changed): the tumbling body is freed and the layers resume.
        wearer.Body.SetBodyVisual(null);
        await Frames(2);
        Check(!tumble.IsTumbling && !tumble.Hull.Active && !wearer.Visual.Stack.IsSuspended(PoseLayer.Gait), "a visual taken off mid-tumble frees its tumbling body and resumes its layers");
        wearer.Body.QueueFree();
        wearer.Visual.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }

    /// <summary>The same scripted knockout drop (with some drift) twice on the same spot: a plain body, then one wearing a character.</summary>
    private async Task TestCapsuleUntouched()
    {
        var origin = new Vector3(110, 0, 0);
        var floor = Floor(origin);
        await Frames(2);
        var paths = new List<List<Vector3>>();
        foreach (var dressed in new[] { false, true })
        {
            SmallPlayerController body;
            if (dressed) body = Wear("tomato_man", origin + new Vector3(0, 1.8f, 0)).Body;
            else
            {
                body = new SmallPlayerController { Name = $"Plain{_serial++}", ReadKeyboard = false, Position = origin + new Vector3(0, 1.8f, 0) };
                AddChild(body);
            }
            await Frames(1);
            body.SetWorldPhysics(Preset("room_tuned", body.WorldPhysicsRevision + 1));
            body.GlobalPosition = origin + new Vector3(0, 1.8f, 0);
            body.Velocity = new Vector3(0.15f, 0, -0.05f);
            var path = new List<Vector3>();
            for (var frame = 0; frame < 220; frame++)
            {
                await Frames(1);
                path.Add(body.GlobalPosition - origin);
            }
            paths.Add(path);
            Check(!dressed || (body.BodyVisual is CharacterVisual visual && CharacterTumble.Find(visual)!.Tumbles == 1), dressed ? "the dressed body tumbled on that drop" : "the plain body dropped");
            body.QueueFree();
            await Frames(2);
        }
        var worst = paths[0].Zip(paths[1], (a, b) => a.DistanceTo(b)).Max();
        Check(worst <= 1e-6f, Inv($"the capsule moves identically with and without the drawn body and its tumble over the same drop (largest difference {worst * 1000:0.000000} mm)"));
        floor.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// Brackets the tumble layer (the stack's last) to count what it allocates on the managed heap: the start probe runs just
    /// before it (a Gesture layer added after the others), the end probe just after it (a Tumble layer added after it).
    /// </summary>
    private sealed class AllocationProbe : IPoseLayer<BodyState>
    {
        private readonly AllocationProbe? _start;
        private readonly CharacterTumble? _tumble;
        public AllocationProbe(PoseLayer layer, AllocationProbe? start = null, CharacterTumble? tumble = null)
        {
            Layer = layer;
            _start = start;
            _tumble = tumble;
        }
        public PoseLayer Layer { get; }
        public long Mark;
        public long Bytes;
        public long GetUpBytes;
        public int Ticks;
        public bool Counting;
        public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
        {
            var now = GC.GetAllocatedBytesForCurrentThread();
            if (_start == null)
            {
                Mark = now;
                return;
            }
            if (!Counting) return;
            Bytes += now - _start.Mark;
            if (_tumble?.Phase == TumblePhase.GetUp) GetUpBytes += now - _start.Mark;
            Ticks++;
        }
    }

    /// <summary>
    /// The tumble's frame cost: 16 bodies (four of each character) standing, then all tumbling, frame time compared through the flop
    /// and through the get-up (review minor 9: the get-up casts its own rays); and what the tumble layer allocates while it plays.
    /// </summary>
    private async Task TestFrameCost(bool skipBudget)
    {
        var origin = new Vector3(120, 0, 0);
        var floor = Floor(origin + new Vector3(1, 0, 1), 6);
        var wearers = new List<Wearer>();
        var probes = new List<AllocationProbe>();
        for (var i = 0; i < 16; i++)
        {
            var wearer = Wear(PlayerCharacters[i % 4], origin + new Vector3(0.5f * (i % 4), 0.003f, 0.5f * (i / 4)));
            var start = new AllocationProbe(PoseLayer.Gesture);
            var end = new AllocationProbe(PoseLayer.Tumble, start, wearer.Tumble);
            wearer.Visual.Stack.Add(start);
            wearer.Visual.Stack.Add(end);
            probes.Add(end);
            wearers.Add(wearer);
        }
        await Frames(30);
        async Task<double> FrameMs(int frames)
        {
            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            for (var f = 0; f < frames; f++)
            {
                var before = clock.Elapsed.TotalMilliseconds;
                await Frames(1);
                times.Add(clock.Elapsed.TotalMilliseconds - before);
            }
            times.Sort();
            return times[times.Count / 2];
        }
        var idle = await FrameMs(45);
        var seed = 11u;
        foreach (var wearer in wearers)
        {
            wearer.Tumble.NextSeed = seed++;
            wearer.Tumble.Tumble(new Vector3(0.3f, 0, 0.2f), TumbleTiers.KnockoutSeconds);
        }
        // The first two ticks make the tumble's one-time things (each character's measured hull, the stars): not counted.
        await Frames(2);
        foreach (var probe in probes) probe.Counting = true;
        var flopping = await FrameMs(45);
        var flopped = wearers.All(w => w.Tumble.Phase == TumblePhase.Flop);
        for (var f = 0; f < 120 && wearers.Any(w => w.Tumble.Phase != TumblePhase.GetUp); f++) await Frames(1);
        var risingAll = wearers.All(w => w.Tumble.Phase == TumblePhase.GetUp);
        var rising = await FrameMs(16);
        var gotUp = wearers.All(w => w.Tumble.Phase == TumblePhase.GetUp);
        foreach (var probe in probes) probe.Counting = false;
        var perFlop = Math.Max(0, flopping - idle) / wearers.Count;
        var perGetUp = Math.Max(0, rising - idle) / wearers.Count;
        var bytes = probes.Sum(p => p.Bytes);
        var getUpBytes = probes.Sum(p => p.GetUpBytes);
        var ticks = probes.Sum(p => p.Ticks);
        GD.Print(Inv($"TUMBLE_COST median frame {idle:0.000} ms with 16 bodies standing, {flopping:0.000} ms with all 16 flopping, {rising:0.000} ms with all 16 getting up: {perFlop:0.000} and {perGetUp:0.000} ms per tumble (budget 0.3 ms); the tumble layer allocated {bytes} bytes over {ticks} ticks ({getUpBytes} getting up)"));
        Check(flopped && risingAll && gotUp, "the cost is sampled while all 16 flop and while all 16 get up");
        Check(bytes == 0 && ticks > 16 * 50, Inv($"the tumble layer allocates nothing per tick while it flops and gets up ({bytes} bytes over {ticks} ticks)"));
        if (!skipBudget) Check(perFlop <= 0.3 && perGetUp <= 0.3, Inv($"a tumble costs at most 0.3 ms of frame time flopping ({perFlop:0.000} ms) and getting up ({perGetUp:0.000} ms)"));
        foreach (var wearer in wearers) wearer.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }
}
