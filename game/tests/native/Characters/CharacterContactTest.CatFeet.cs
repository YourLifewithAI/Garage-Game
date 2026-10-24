using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Characters;

/// <summary>The landing tiers under a Cat feet safe drop (docs/runs/RUN-2-MECHANICS.md, M5): the contact layer reads the body's own cost.</summary>
public partial class CharacterContactTest
{
    /// <summary>
    /// The same drops as TestLandings with a 0.60 m safe drop: 0.4 m is a squash and no stumble (it stumbles with none), 0.7 m a
    /// stumble (a tumble with none), 1.0 m the tumble's. The tiers are the layer's own thresholds, in health points, read from the
    /// vitals: the contact layer's tumble tier is the tumble's.
    /// </summary>
    private async Task TestLandingsWithCatFeet()
    {
        Check(ContactLayer.TumbleFromHealth == TumbleTiers.TumbleHealth && TumbleTiers.StumbleHealth == 0 && TumbleTiers.KnockoutHealth == PlayerVitals.MaximumHealth,
            "the contact layer's tumble threshold is the tumble's own: one number, 25 health, read from the vitals");
        var cat = new BodyKnacks { SafeDropM = 0.60f };
        foreach (var (height, expected, without) in new[] { (0.4f, LandingTier.Squash, LandingTier.Stumble), (0.7f, LandingTier.Stumble, LandingTier.Tumble), (1.0f, LandingTier.Tumble, LandingTier.Tumble) })
        {
            var before = new Dictionary<string, int>();
            var stumbleFrames = new Dictionary<string, int>();
            foreach (var s in Walkers) Place(s.Body, s.Course.Flat, 0);
            await Frames(20);
            foreach (var s in Walkers)
            {
                before[s.Id] = s.Contact.Landings;
                stumbleFrames[s.Id] = 0;
                s.Body.Knacks = cat;
                s.Body.GlobalPosition = s.Course.Flat + Vector3.Up * height;
                s.Body.Velocity = Vector3.Zero;
                s.Body.ResetVitalsObservation();
            }
            for (var f = 0; f < 150; f++)
            {
                await Frames(1);
                foreach (var s in Walkers)
                    if (s.Contact.Landings != before[s.Id] && s.Contact.Stumbling) stumbleFrames[s.Id]++;
            }
            foreach (var s in Walkers)
            {
                var c = s.Contact;
                var speed = c.LastLandingSpeedMps;
                var plain = PlayerVitals.FallDamage(speed, false);
                var damage = cat.FallDamage(speed, false);
                var tier = damage <= 0 ? LandingTier.Squash : damage < ContactLayer.TumbleFromHealth ? LandingTier.Stumble : LandingTier.Tumble;
                var plainTier = plain <= 0 ? LandingTier.Squash : plain < ContactLayer.TumbleFromHealth ? LandingTier.Stumble : LandingTier.Tumble;
                Report(c.Landings == before[s.Id] + 1 && c.LastTier == expected && tier == expected && Mathf.IsEqualApprox(c.LastLandingDamage, damage, 1e-3f) && plainTier == without,
                    FormattableString.Invariant($"{s.Id} dropped {height:0.0} m with a 0.60 m safe drop: lands at {speed:0.00} m/s, {damage:0.0} health (and {plain:0.0} with none), a {c.LastTier.ToString().ToLowerInvariant()} where none would give a {plainTier.ToString().ToLowerInvariant()}"));
                if (expected == LandingTier.Squash) Check(stumbleFrames[s.Id] == 0, $"{s.Id}: a cat lands softly, no stumble");
                s.Body.Knacks = BodyKnacks.None;
            }
            await Frames(10);
        }
    }
}
