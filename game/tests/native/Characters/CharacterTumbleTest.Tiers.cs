using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Characters;

/// <summary>The tiers, from PlayerVitals.FallDamage on real falls; the installer; deep water, a glide and a floating body.</summary>
public partial class CharacterTumbleTest
{
    /// <summary>The landing speed at which a fall would cost <paramref name="health"/> (FallDamage's inverse).</summary>
    private static float SpeedFor(float health) => PlayerVitals.SafeLandingSpeedMps + health / PlayerVitals.FallDamagePerMps;

    private void TestTierTable()
    {
        Check(TumbleTiers.StumbleHealth == 0 && TumbleTiers.TumbleHealth == 25 && TumbleTiers.TumbleHealth == PlayerVitals.MaximumHealth / 4 && TumbleTiers.KnockoutHealth == PlayerVitals.MaximumHealth &&
              TumbleTiers.KnockoutHealth == 100 && GarageGame.Native.Characters.Contact.ContactLayer.TumbleFromHealth == TumbleTiers.TumbleHealth,
            "the tiers are three numbers in one place, health points read from the vitals: past the safe drop, 25 and 100 (the contact layer's is the same number)");
        Check(Mathf.IsEqualApprox(TumbleTiers.Seconds(TumbleTier.Tumble), 1.2f) && Mathf.IsEqualApprox(TumbleTiers.Seconds(TumbleTier.Knockout), 2.0f) &&
              TumbleTiers.Seconds(TumbleTier.Stumble) == 0 && TumbleTiers.Seconds(TumbleTier.None) == 0,
            "a tumble lasts about 1.2 s, a knockout's about 2 s; below the tumble tier the contact layer answers");
        var cases = new (float Speed, bool Deep, TumbleTier Tier)[]
        {
            (0, false, TumbleTier.None), (PlayerVitals.SafeLandingSpeedMps - 0.01f, false, TumbleTier.None),
            (SpeedFor(1), false, TumbleTier.Stumble), (SpeedFor(24.9f), false, TumbleTier.Stumble),
            (SpeedFor(25.05f), false, TumbleTier.Tumble), (SpeedFor(99.9f), false, TumbleTier.Tumble),
            (SpeedFor(100.05f), false, TumbleTier.Knockout), (12f, false, TumbleTier.Knockout),
            (12f, true, TumbleTier.None), (SpeedFor(50), true, TumbleTier.None), (float.NaN, false, TumbleTier.None), (-3f, false, TumbleTier.None),
        };
        foreach (var (speed, deep, tier) in cases)
            Check(TumbleTiers.Classify(speed, deep) == tier && TumbleTiers.Classify(PlayerVitals.FallDamage(speed, deep)) == tier,
                Inv($"tiers: a landing at {speed:0.000} m/s{(deep ? " into deep water" : "")} is {tier} (FallDamage {PlayerVitals.FallDamage(speed, deep):0.0}), got {TumbleTiers.Classify(speed, deep)}"));
    }

    private async Task TestInstall()
    {
        var floor = Floor(new Vector3(30, 0, 0));
        var wearer = Wear("cloudpuff", new Vector3(30, 0.003f, 0));
        await Frames(3);
        var stack = wearer.Visual.Stack;
        TumbleInstaller.Install(wearer.Visual, wearer.Body);
        var count = Enumerable.Range(0, stack.Count).Count(i => stack[i] is CharacterTumble);
        Check(count == 1 && stack[stack.Count - 1] is CharacterTumble && wearer.Tumble.Layer == PoseLayer.Tumble,
            "the installer adds one tumble layer, the stack's last, and installing again changes nothing");
        Check(Mathf.IsEqualApprox(wearer.Body.KnockoutRecoveryDelayS, TumbleTiers.KnockoutSeconds),
            $"the installer sets the body's KnockoutRecoveryDelayS to the knockout tumble's length ({wearer.Body.KnockoutRecoveryDelayS} s)");
        wearer.Body.SetBodyVisual(null);
        await Frames(1);
        Check(wearer.Body.KnockoutRecoveryDelayS == 0, "with the drawn body taken off (the prototype figure), the recovery no longer waits");
        wearer.Body.SetBodyVisual(wearer.Visual);
        await Frames(1);
        Check(Mathf.IsEqualApprox(wearer.Body.KnockoutRecoveryDelayS, TumbleTiers.KnockoutSeconds), "put back on, it waits for the knockout tumble again");
        // The once-per-character measurement of Jolt's surface met every ray (a tumble starts it).
        wearer.Tumble.NextSeed = 7;
        Check(wearer.Tumble.Tumble(Vector3.Right * 0.2f, 0.8f), "a standing body can be knocked over");
        await Frames(2);
        Check(wearer.Tumble.Hull.MeasuredRays == wearer.Tumble.Hull.Points && wearer.Tumble.Hull.MaxInsetM is >= 0 and < 0.01f,
            Inv($"Jolt's own surface is measured along every hull point's ray ({wearer.Tumble.Hull.MeasuredRays}/{wearer.Tumble.Hull.Points}; deepest {wearer.Tumble.Hull.MaxInsetM * 1000:0.00} mm inside the points)"));
        var hulls = new List<TumbleHull>();
        foreach (var id in PlayerCharacters)
        {
            var spare = CharacterVisual.Create(Definition(id)).Body;
            hulls.Add(TumbleHull.For(spare));
            spare.Free();
        }
        Check(hulls.All(h => h.Points <= TumbleHull.MaxHullPoints && h.Points >= 4 && h.Mass > 0),
            "every hull keeps within Jolt's 256 vertices: " + string.Join(", ", PlayerCharacters.Zip(hulls, (id, h) => Inv($"{id} {h.Points} points, {h.Mass * 1000:0.0} g"))));
        await Frames(60);
        wearer.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }

    private sealed class Drop
    {
        public required Wearer Wearer;
        public required float Height;
        public float Landing;
        public bool Deep;
        public int Observed;
    }

    /// <summary>Drops a wearer of each character in turn from each height onto its own spot of one floor, and waits for every landing.</summary>
    private async Task<List<Drop>> DropAll(Vector3 origin, string preset, IReadOnlyList<float> heights, Action<SmallPlayerController>? prepare = null, int framesAfter = 4)
    {
        var drops = new List<Drop>();
        for (var i = 0; i < heights.Count; i++)
        {
            var wearer = Wear(PlayerCharacters[i % PlayerCharacters.Length], origin + new Vector3(0.5f * i, heights[i], 0));
            drops.Add(new Drop { Wearer = wearer, Height = heights[i] });
        }
        await Frames(1);
        foreach (var drop in drops)
        {
            var body = drop.Wearer.Body;
            body.SetWorldPhysics(Preset(preset, body.WorldPhysicsRevision + 1));
            body.GlobalPosition = origin + new Vector3(0.5f * drops.IndexOf(drop), drop.Height, 0);
            body.Velocity = Vector3.Zero;
            body.ResetVitalsObservation();
            prepare?.Invoke(body);
            var observed = drop;
            body.FallObserved = (speed, deep) =>
            {
                if (speed <= 0) return;
                observed.Landing = Mathf.Max(observed.Landing, speed);
                observed.Deep |= deep;
                observed.Observed++;
            };
        }
        for (var frame = 0; frame < 300 && drops.Any(d => d.Observed == 0); frame++) await Frames(1);
        await Frames(framesAfter);
        return drops;
    }

    private async Task TestTiersOnRealFalls()
    {
        foreach (var (preset, gravity, heights) in new[]
                 {
                     ("room_tuned", 3.5f, new[] { 0.2f, 0.45f, 0.7f, 1.3f, 1.75f, 0.55f, 1.55f, 2.2f }),
                     ("room_real", 9.8f, new[] { 0.08f, 0.15f, 0.3f, 0.45f, 0.7f, 0.2f, 0.56f, 1.0f }),
                 })
        {
            var origin = new Vector3(40, 0, gravity < 5 ? 10 : 20);
            var floor = Floor(origin + new Vector3(2, 0, 0), 8);
            await Frames(1);
            var drops = await DropAll(origin, preset, heights);
            var tiers = new HashSet<TumbleTier>();
            foreach (var drop in drops)
            {
                var tumble = drop.Wearer.Tumble;
                var expected = TumbleTiers.Classify(PlayerVitals.FallDamage(drop.Landing, drop.Deep));
                tiers.Add(expected);
                Check(drop.Observed > 0 && tumble.LastTier == expected && Mathf.IsEqualApprox(tumble.LastLandingCost, PlayerVitals.FallDamage(drop.Landing, false), 1e-3f),
                    Inv($"{preset} ({gravity} m/s²) {drop.Height:0.00} m: the tumble's tier is FallDamage's ({drop.Landing:0.000} m/s costs {PlayerVitals.FallDamage(drop.Landing, false):0.0}: {expected}), got {tumble.LastTier} ({tumble.LastLandingCost:0.0})"));
                var tumbles = expected >= TumbleTier.Tumble;
                Check(tumble.Tumbles == (tumbles ? 1 : 0) && tumble.IsTumbling == tumbles && drop.Wearer.Body.ControlHeld == tumbles &&
                      tumble.Dazed == (expected == TumbleTier.Knockout) && (!tumbles || Mathf.IsEqualApprox(tumble.Seconds, TumbleTiers.Seconds(expected))),
                    Inv($"{preset} {drop.Height:0.00} m ({expected}): tumbling {tumble.IsTumbling} for {tumble.Seconds:0.0} s, control held {drop.Wearer.Body.ControlHeld}, dazed {tumble.Dazed}"));
            }
            Check(tiers.SetEquals(new[] { TumbleTier.None, TumbleTier.Stumble, TumbleTier.Tumble, TumbleTier.Knockout }),
                $"{preset}: the drops cover every tier: {string.Join(", ", tiers.OrderBy(t => t))}");
            await Frames(140);
            Check(drops.All(d => !d.Wearer.Tumble.IsTumbling && !d.Wearer.Body.ControlHeld), $"{preset}: every tumble is over after its length, and control is back");
            foreach (var drop in drops) drop.Wearer.Body.QueueFree();
            floor.QueueFree();
            await Frames(2);
        }
    }

    private async Task TestDeepWaterAndGlide()
    {
        var origin = new Vector3(60, 0, 0);
        var floor = Floor(origin + new Vector3(1, 0, 0), 6);
        await Frames(1);
        // A glide (a worn glider's 0.5 m/s fall limit) makes a knockout's height safe: no tumble.
        var glides = await DropAll(origin, "room_tuned", new[] { 1.75f, 1.75f },
            body => body.SetCreationEffects(Vector3.Zero, 0.5f, Callable.From<Vector3, bool>(_ => true)));
        Check(glides.All(d => d.Observed > 0 && d.Landing <= 0.51f && d.Wearer.Tumble.Tumbles == 0 && d.Wearer.Tumble.LastTier == TumbleTier.None && !d.Wearer.Body.ControlHeld),
            Inv($"under a glide a 1.75 m fall lands slowly and never tumbles ({string.Join(", ", glides.Select(d => $"{d.Landing:0.000} m/s"))})"));
        foreach (var drop in glides) drop.Wearer.Body.QueueFree();
        // Deep water catches a knockout's fall: never a tumble.
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-1, -1), new Vector2(3, -1), new Vector2(3, 1), new Vector2(-1, -1), new Vector2(3, 1), new Vector2(-1, 1) })
            sheet.AddVertex(origin + new Vector3(corner.X, 0.2f, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        AddChild(water);
        await Frames(2);
        var deep = await DropAll(origin, "room_tuned", new[] { 1.9f, 1.9f, 1.9f, 1.9f }, framesAfter: 10);
        Check(deep.All(d => d.Observed > 0 && d.Deep && d.Wearer.Tumble.Tumbles == 0 && !d.Wearer.Tumble.IsTumbling && !d.Wearer.Body.ControlHeld),
            Inv($"deep water catches a 1.9 m fall ({string.Join(", ", deep.Select(d => $"{d.Landing:0.00} m/s{(d.Deep ? " deep" : "")}"))}) and no character tumbles"));
        // Review minor 8: the landing the tumble reads into deep water is the fall's real speed (one that would tumble on dry ground),
        // so it is the deep water, not a speed of 0, that keeps the tiers from tumbling.
        Check(deep.All(d => d.Wearer.Tumble.LastTier == TumbleTier.None && TumbleTiers.Classify(d.Wearer.Tumble.LastLandingSpeedMps, false) >= TumbleTier.Tumble),
            Inv($"the deep-water landings reach the tumble at their falling speed ({string.Join(", ", deep.Select(d => $"{d.Wearer.Tumble.LastLandingSpeedMps:0.00} m/s"))}), a tumble's on dry ground"));
        Check(deep.All(d => !d.Wearer.Body.IsSwimming || !d.Wearer.Tumble.Tumble(Vector3.Right, 1)),
            "a swimmer is never knocked over by a hazard either");
        foreach (var drop in deep) drop.Wearer.Body.QueueFree();
        water.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }

    private async Task TestFloatingNeverTumbles()
    {
        var floor = Floor(new Vector3(70, 0, 0));
        var gubble = Wear("gubble", new Vector3(70, 0.05f, 0), floats: true);
        await Frames(10);
        Check(gubble.Body.KnockoutRecoveryDelayS == 0, "a floating body (the Gubble) never delays a recovery for a tumble it never has");
        Check(!gubble.Tumble.Tumble(new Vector3(1, 0, 0), 2), "a floating body cannot be knocked over");
        gubble.Body.GlobalPosition = new Vector3(70, 1.5f, 0);
        await Frames(90);
        Check(gubble.Tumble.Tumbles == 0 && !gubble.Tumble.IsTumbling && !gubble.Body.ControlHeld && !gubble.Tumble.Hull.Active,
            "a floating body moved high above the floor floats down and never tumbles");
        gubble.Body.QueueFree();
        floor.QueueFree();
        await Frames(2);
    }
}
