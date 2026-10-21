using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Characters;

/// <summary>The tumble under a Cat feet safe drop (docs/runs/RUN-2-MECHANICS.md, M5; RUN-2-CHARACTERS.md, "What counts as a big fall").</summary>
public partial class CharacterTumbleTest
{
    /// <summary>
    /// Real falls of drawn bodies with safe drops of 0.45 m and 0.75 m: the tier is the body's own cost (the vitals' curve started at
    /// the safe drop), at the same 25 and 100 health, so the tumble's heights move up with the knack and nothing else does.
    /// </summary>
    private async Task TestCatFeetTiers()
    {
        foreach (var (name, safe, heights) in new[]
                 {
                     ("cat_feet:1", 0.45f, new[] { 0.4f, 0.55f, 0.6f, 0.8f, 1.0f, 1.4f, 2.1f, 2.5f }),
                     ("cat_feet:3", 0.75f, new[] { 0.7f, 0.9f, 0.95f, 1.2f, 1.5f, 1.9f, 2.7f, 3.0f }),
                 })
        {
            var knacks = new BodyKnacks { SafeDropM = safe };
            var origin = new Vector3(90, 0, safe < 0.5f ? 10 : 30);
            var floor = Floor(origin + new Vector3(2, 0, 0), 8);
            await Frames(1);
            var drops = await DropAll(origin, "room_tuned", heights, body => body.Knacks = knacks);
            var tiers = new HashSet<TumbleTier>();
            foreach (var drop in drops)
            {
                var tumble = drop.Wearer.Tumble;
                var cost = knacks.FallDamage(drop.Landing, drop.Deep);
                var expected = TumbleTiers.Classify(cost);
                tiers.Add(expected);
                Check(drop.Observed > 0 && tumble.LastTier == expected && Mathf.IsEqualApprox(tumble.LastLandingCost, cost, 1e-3f) && expected == TumbleTiers.Classify(drop.Landing, drop.Deep, knacks),
                    Inv($"{name} ({safe:0.00} m) {drop.Height:0.00} m: the tumble's tier is the body's own cost's ({drop.Landing:0.000} m/s costs {cost:0.0}: {expected}), got {tumble.LastTier} ({tumble.LastLandingCost:0.0})"));
                var tumbles = expected >= TumbleTier.Tumble;
                Check(tumble.Tumbles == (tumbles ? 1 : 0) && tumble.IsTumbling == tumbles && drop.Wearer.Body.ControlHeld == tumbles && tumble.Dazed == (expected == TumbleTier.Knockout),
                    Inv($"{name} {drop.Height:0.00} m ({expected}): tumbling {tumble.IsTumbling}, control held {drop.Wearer.Body.ControlHeld}, dazed {tumble.Dazed}"));
            }
            Check(tiers.SetEquals(new[] { TumbleTier.None, TumbleTier.Stumble, TumbleTier.Tumble, TumbleTier.Knockout }),
                $"{name}: the drops cover every tier: {string.Join(", ", tiers.OrderBy(t => t))}");
            await Frames(160);
            Check(drops.All(d => !d.Wearer.Tumble.IsTumbling && !d.Wearer.Body.ControlHeld), $"{name}: every tumble is over after its length, and control is back");
            foreach (var drop in drops) drop.Wearer.Body.QueueFree();
            floor.QueueFree();
            await Frames(2);
        }
        // The same 0.7 m fall without the knack is a tumble.
        var plainOrigin = new Vector3(90, 0, 50);
        var plainFloor = Floor(plainOrigin + new Vector3(2, 0, 0), 8);
        await Frames(1);
        var plain = await DropAll(plainOrigin, "room_tuned", new[] { 0.7f, 0.7f });
        Check(plain.All(d => d.Wearer.Tumble.LastTier == TumbleTier.Tumble && d.Wearer.Tumble.Tumbles == 1), "without a knack the same 0.7 m fall is a tumble");
        await Frames(160);
        foreach (var drop in plain) drop.Wearer.Body.QueueFree();
        plainFloor.QueueFree();
        await Frames(2);
    }
}
