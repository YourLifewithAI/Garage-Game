using System;
using System.Collections.Generic;
using System.Linq;
using static GarageGame.Native.Audio.Dsp;

namespace GarageGame.Native.Audio;

/// <summary>The ids of every sound the game makes, and the bus each plays on.</summary>
public static class Sounds
{
    // ambience (loops)
    public const string WindSoft = "wind_soft";
    public const string WindGust = "wind_gust";
    public const string Sea = "sea";
    public const string Rain = "rain";
    public const string Insects = "insects";
    public const string Fire = "fire";
    // birds (one-shots, by day)
    public const string BirdA = "bird_a";
    public const string BirdB = "bird_b";
    public const string BirdC = "bird_c";
    // feet, two variants each: step_<ground>_0 and step_<ground>_1
    public const string StepGrass = "step_grass";
    public const string StepDirt = "step_dirt";
    public const string StepSand = "step_sand";
    public const string StepStone = "step_stone";
    public const string StepWood = "step_wood";
    // water
    public const string Wade = "wade";
    public const string Swim = "swim";
    public const string Splash = "splash";
    // hands and mouth
    public const string Pickup = "pickup";
    public const string Putdown = "putdown";
    public const string Eat = "eat";
    // ui
    public const string UiTick = "ui_tick";
    public const string UiHover = "ui_hover";
    public const string UiConfirm = "ui_confirm";

    public static readonly string[] Grounds = { "grass", "dirt", "sand", "stone", "wood" };
    public static readonly string[] Birds = { BirdA, BirdB, BirdC };
    public static string Step(string ground, int variant) => $"step_{ground}_{variant & 1}";
}

/// <summary>One sound: its id, the bus it plays on, whether it loops, the loudest sample it may have, and how to make it.</summary>
public sealed record SoundRecipe(string Id, string Bus, bool Loop, float Peak, Func<float[]> Make);

/// <summary>
/// Every sound in the game, made from code (no audio file, sample or library). Each recipe is pure and deterministic, runs on a worker thread
/// and returns mono float samples at Dsp.Rate. Loops are filtered circularly and their events wrap, so they repeat without a click; one-shots
/// fade in and out so they start and end at silence. Peaks are well under full scale (loops about -10 dBFS, effects about -7 dBFS):
/// these are for children, and the buses and a hard limiter on Master bound the rest.
/// </summary>
public static class SoundBank
{
    private static readonly Lazy<IReadOnlyList<SoundRecipe>> All_ = new(Build);
    public static IReadOnlyList<SoundRecipe> All => All_.Value;
    public static SoundRecipe? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    /// <summary>The finished samples of a sound (made now, on the calling thread).</summary>
    public static float[] Render(SoundRecipe recipe)
    {
        var samples = recipe.Make();
        if (recipe.Loop) RemoveDc(samples); else Fade(samples);
        return Normalize(samples, recipe.Peak);
    }

    private static IReadOnlyList<SoundRecipe> Build()
    {
        var list = new List<SoundRecipe>
        {
            new(Sounds.WindSoft, AudioBuses.Ambience, true, 0.30f, () => Wind(soft: true)),
            new(Sounds.WindGust, AudioBuses.Ambience, true, 0.30f, () => Wind(soft: false)),
            new(Sounds.Sea, AudioBuses.Ambience, true, 0.30f, SeaLoop),
            new(Sounds.Rain, AudioBuses.Ambience, true, 0.28f, RainLoop),
            new(Sounds.Insects, AudioBuses.Ambience, true, 0.16f, InsectLoop),
            new(Sounds.Fire, AudioBuses.Effects, true, 0.32f, FireLoop),
            new(Sounds.BirdA, AudioBuses.Ambience, false, 0.22f, BirdTweets),
            new(Sounds.BirdB, AudioBuses.Ambience, false, 0.22f, BirdWhistle),
            new(Sounds.BirdC, AudioBuses.Ambience, false, 0.20f, BirdTrill),
            new(Sounds.Wade, AudioBuses.Effects, false, 0.40f, WadeStep),
            new(Sounds.Swim, AudioBuses.Effects, false, 0.38f, SwimStroke),
            new(Sounds.Splash, AudioBuses.Effects, false, 0.45f, SplashHit),
            new(Sounds.Pickup, AudioBuses.Effects, false, 0.38f, PickupPop),
            new(Sounds.Putdown, AudioBuses.Effects, false, 0.38f, PutdownThud),
            new(Sounds.Eat, AudioBuses.Effects, false, 0.40f, EatCrunch),
            new(Sounds.UiTick, AudioBuses.UI, false, 0.28f, () => Tone(0.05, 1400, 0.0015f, 0.012f)),
            new(Sounds.UiHover, AudioBuses.UI, false, 0.14f, () => Tone(0.03, 1900, 0.001f, 0.006f)),
            new(Sounds.UiConfirm, AudioBuses.UI, false, 0.30f, ConfirmTones),
        };
        for (uint v = 0; v < 2; v++)
        {
            var seed = v;
            list.Add(new($"step_grass_{v}", AudioBuses.Effects, false, 0.34f, () => StepGrass(seed)));
            list.Add(new($"step_dirt_{v}", AudioBuses.Effects, false, 0.36f, () => StepDirt(seed)));
            list.Add(new($"step_sand_{v}", AudioBuses.Effects, false, 0.30f, () => StepSand(seed)));
            list.Add(new($"step_stone_{v}", AudioBuses.Effects, false, 0.34f, () => StepStone(seed)));
            list.Add(new($"step_wood_{v}", AudioBuses.Effects, false, 0.38f, () => StepWood(seed)));
        }
        return list;
    }

    private static float Env(float t, float attack, float decay) => MathF.Min(1f, t / attack) * MathF.Exp(-t / decay);

    private static float[] Noise(int length, uint seed)
    {
        var rng = new Rng(seed);
        var noise = new float[length];
        for (var i = 0; i < length; i++) noise[i] = rng.Signed();
        return noise;
    }

    // ---- ambience ----

    /// <summary>Wind: low noise whose brightness and strength swell once or twice around an 8 s loop. Soft is a hush, gust is airier.</summary>
    private static float[] Wind(bool soft)
    {
        var buffer = Buffer(8);
        var noise = Noise(buffer.Length, soft ? 11u : 12u);
        var a = new LowPass(300); var b = new LowPass(300);
        var cycles = soft ? 1 : 2;
        Circular(buffer, i =>
        {
            var t = Time(i) / 8f;
            var swell = 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * (t * cycles + (soft ? 0.1f : 0.35f)));
            var cutoff = soft ? 220 + 280 * swell : 380 + 1100 * swell;
            a.Set(cutoff); b.Set(cutoff);
            var y = b.Process(a.Process(noise[i]));
            return y * (soft ? 0.7f + 0.3f * swell : 0.45f + 0.55f * swell);
        });
        return buffer;
    }

    /// <summary>The sea: a low wash that swells and slides back every 6 s, with a hiss at each crest.</summary>
    private static float[] SeaLoop()
    {
        var buffer = Buffer(12);
        var wash = Noise(buffer.Length, 21);
        var foam = Noise(buffer.Length, 22);
        var l1 = new LowPass(650); var l2 = new LowPass(650);
        var h = new HighPass(1500); var hl = new LowPass(4500);
        Circular(buffer, i =>
        {
            var t = Time(i);
            var swell = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * t / 6f);
            var slow = 0.75f + 0.25f * MathF.Sin(2 * MathF.PI * t / 12f);
            var crest = MathF.Pow(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * (t - 0.6f) / 6f), 3f);
            var body = l2.Process(l1.Process(wash[i])) * (0.2f + 0.8f * swell) * slow;
            var hiss = hl.Process(h.Process(foam[i])) * crest * 0.35f;
            return body * 2.2f + hiss;
        });
        return buffer;
    }

    /// <summary>Rain: a soft sheet of filtered noise with small drops pattering over it.</summary>
    private static float[] RainLoop()
    {
        var buffer = Buffer(6);
        var noise = Noise(buffer.Length, 31);
        var hp = new HighPass(700); var lp = new LowPass(4200);
        Circular(buffer, i => lp.Process(hp.Process(noise[i])) * 0.8f);
        var rng = new Rng(32);
        var drops = (int)(6 * 70);
        for (var d = 0; d < drops; d++)
        {
            var start = rng.Range(0f, 6f);
            var freq = rng.Range(2200f, 4800f);
            var amp = rng.Range(0.08f, 0.35f);
            var tick = new float[(int)(0.012 * Rate)];
            for (var i = 0; i < tick.Length; i++)
            {
                var t = Time(i);
                tick[i] = MathF.Sin(2 * MathF.PI * freq * t) * MathF.Exp(-t / 0.003f) * amp;
            }
            Mix(buffer, tick, start, 1f, wrap: true);
        }
        return buffer;
    }

    /// <summary>Night insects: three soft, high voices, each a short run of pulses repeating on a period that divides the 6 s loop.</summary>
    private static float[] InsectLoop()
    {
        var buffer = Buffer(6);
        void Voice(double period, double offset, float freq, int pulses, double spacing, double pulseLength, float gain)
        {
            var pulse = new float[(int)(pulseLength * Rate)];
            for (var i = 0; i < pulse.Length; i++)
            {
                var t = Time(i);
                var hann = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (pulse.Length - 1));
                pulse[i] = MathF.Sin(2 * MathF.PI * freq * t) * hann;
            }
            for (var group = 0; group * period < 6.0 - 1e-6; group++)
                for (var p = 0; p < pulses; p++)
                    Mix(buffer, pulse, (offset + group * period + p * spacing) % 6.0, gain, wrap: true);
        }
        Voice(0.75, 0.10, 3300, 4, 0.045, 0.024, 1.0f);
        Voice(1.2, 0.45, 3750, 3, 0.060, 0.030, 0.7f);
        Voice(0.6, 0.30, 2950, 2, 0.050, 0.026, 0.5f);
        return buffer;
    }

    /// <summary>The campfire: a low flutter, a faint hiss and now and then a pop.</summary>
    private static float[] FireLoop()
    {
        var buffer = Buffer(10);
        var rumble = Noise(buffer.Length, 41);
        var hiss = Noise(buffer.Length, 42);
        var flick = Noise(buffer.Length, 43);
        var r1 = new LowPass(170); var r2 = new LowPass(170);
        var hh = new HighPass(1400); var hl = new LowPass(4200);
        var fl = new LowPass(7);
        Circular(buffer, i =>
        {
            var t = Time(i);
            var slow = 0.7f + 0.3f * MathF.Sin(2 * MathF.PI * 3 * t / 10f);
            var f = 0.55f + 4f * fl.Process(flick[i]);
            return r2.Process(r1.Process(rumble[i])) * 2.6f * slow + hl.Process(hh.Process(hiss[i])) * 0.16f * Math.Clamp(f, 0.2f, 1.2f);
        });
        var rng = new Rng(44);
        for (var p = 0; p < 62; p++)
        {
            var length = (int)(rng.Range(0.003f, 0.014f) * Rate);
            var big = rng.Next01() < 0.12f;
            var amp = big ? rng.Range(0.7f, 1.0f) : rng.Range(0.12f, 0.5f);
            var pop = new float[length];
            var n = new Rng(100u + (uint)p);
            var ph = new HighPass(1200); var pl = new LowPass(5200);
            for (var i = 0; i < length; i++)
                pop[i] = pl.Process(ph.Process(n.Signed())) * MathF.Exp(-Time(i) / (length / (float)Rate / 3f)) * amp;
            Mix(buffer, pop, rng.Range(0f, 10f), 0.9f, wrap: true);
        }
        return buffer;
    }

    // ---- birds ----

    private static float[] Chirp(double seconds, float f0, float f1, float vibratoHz = 0, float vibratoDepth = 0, float harmonic = 0.2f)
    {
        var buffer = Buffer(seconds);
        double phase = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var u = (float)i / (buffer.Length - 1);
            var f = f0 + (f1 - f0) * u + vibratoDepth * MathF.Sin(2 * MathF.PI * vibratoHz * Time(i));
            phase += 2 * Math.PI * f / Rate;
            var env = MathF.Pow(MathF.Sin(MathF.PI * u), 2f);
            buffer[i] = (MathF.Sin((float)phase) + harmonic * MathF.Sin(2 * (float)phase)) * env;
        }
        return buffer;
    }

    private static float[] BirdTweets()
    {
        var buffer = Buffer(0.75);
        Mix(buffer, Chirp(0.12, 2600, 3400), 0.0, 1.0f);
        Mix(buffer, Chirp(0.12, 2700, 3500), 0.20, 0.85f);
        Mix(buffer, Chirp(0.14, 2500, 3600), 0.40, 0.7f);
        return buffer;
    }

    private static float[] BirdWhistle() => Chirp(0.7, 2300, 1700, vibratoHz: 17, vibratoDepth: 70, harmonic: 0.1f);

    private static float[] BirdTrill()
    {
        var buffer = Buffer(0.5);
        for (var p = 0; p < 8; p++)
            Mix(buffer, Chirp(0.026, p % 2 == 0 ? 2900 : 3300, p % 2 == 0 ? 3100 : 3400, harmonic: 0.1f), p * 0.042, 0.55f + 0.45f * MathF.Sin(MathF.PI * p / 7f));
        return buffer;
    }

    // ---- feet ----

    private static float[] StepGrass(uint v)
    {
        var buffer = Buffer(0.17);
        var n = Noise(buffer.Length, 51 + v);
        var hp = new HighPass(1500 + 300 * v); var lp = new LowPass(4200 - 500 * v);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            buffer[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.006f, 0.045f) * 1.2f + MathF.Sin(2 * MathF.PI * 110 * t) * Env(t, 0.003f, 0.03f) * 0.35f;
        }
        return buffer;
    }

    private static float[] StepDirt(uint v)
    {
        var buffer = Buffer(0.16);
        var n = Noise(buffer.Length, 61 + v);
        var hp = new HighPass(200); var lp = new LowPass(1500 + 200 * v);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            buffer[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.004f, 0.038f) * 1.4f + MathF.Sin(2 * MathF.PI * (90 + 8 * v) * t) * Env(t, 0.003f, 0.035f) * 0.55f;
        }
        return buffer;
    }

    private static float[] StepSand(uint v)
    {
        var buffer = Buffer(0.24);
        var n = Noise(buffer.Length, 71 + v);
        var g = Noise(buffer.Length, 72 + v);
        var hp = new HighPass(900); var lp = new LowPass(3500);
        var grain = new LowPass(220);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            var gr = Math.Clamp(0.7f + 5f * grain.Process(g[i]), 0.35f, 1.2f);
            buffer[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.015f, 0.09f) * gr + MathF.Sin(2 * MathF.PI * 100 * t) * Env(t, 0.004f, 0.03f) * 0.15f;
        }
        return buffer;
    }

    private static float[] StepStone(uint v)
    {
        var buffer = Buffer(0.13);
        var n = Noise(buffer.Length, 81 + v);
        var hp = new HighPass(2000); var lp = new LowPass(6000);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            buffer[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.001f, 0.004f) * 0.8f
                      + MathF.Sin(2 * MathF.PI * (700 + 80 * v) * t) * Env(t, 0.001f, 0.018f) * 0.5f
                      + MathF.Sin(2 * MathF.PI * 140 * t) * Env(t, 0.002f, 0.04f) * 0.5f;
        }
        return buffer;
    }

    private static float[] StepWood(uint v)
    {
        var buffer = Buffer(0.2);
        var n = Noise(buffer.Length, 91 + v);
        var lp = new LowPass(1200);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            buffer[i] = MathF.Sin(2 * MathF.PI * (190 + 15 * v) * t) * Env(t, 0.002f, 0.055f)
                      + MathF.Sin(2 * MathF.PI * (290 + 20 * v) * t) * Env(t, 0.002f, 0.04f) * 0.6f
                      + lp.Process(n[i]) * Env(t, 0.001f, 0.015f) * 0.4f;
        }
        return buffer;
    }

    // ---- water ----

    private static float[] WadeStep()
    {
        var buffer = Buffer(0.38);
        var n = Noise(buffer.Length, 101);
        var hp = new HighPass(500); var lp = new LowPass(2500);
        double phase = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            var gurgle = 300 + 400 * MathF.Min(1f, t / 0.3f);
            phase += 2 * Math.PI * gurgle / Rate;
            buffer[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.02f, 0.12f) + MathF.Sin((float)phase) * Env(t, 0.05f, 0.08f) * 0.12f;
        }
        return buffer;
    }

    private static float[] SwimStroke()
    {
        var buffer = Buffer(0.7);
        var n = Noise(buffer.Length, 111);
        var d = Noise(buffer.Length, 112);
        var lp = new LowPass(900); var hp = new HighPass(2200); var hl = new LowPass(5000);
        for (var i = 0; i < buffer.Length; i++)
        {
            var u = (float)i / buffer.Length;
            var slosh = MathF.Pow(MathF.Sin(MathF.PI * u), 2f);
            buffer[i] = lp.Process(n[i]) * slosh * 1.6f + hl.Process(hp.Process(d[i])) * slosh * slosh * 0.25f;
        }
        return buffer;
    }

    private static float[] SplashHit()
    {
        var buffer = Buffer(0.8);
        var n = Noise(buffer.Length, 121);
        var lp = new LowPass(4000);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            lp.Set(500 + 3500 * MathF.Exp(-t / 0.12f));
            buffer[i] = lp.Process(n[i]) * Env(t, 0.005f, 0.17f);
        }
        var rng = new Rng(122);
        for (var b = 0; b < 6; b++)
        {
            var f0 = rng.Range(400f, 750f);
            var bubble = Chirp(0.05, f0, f0 * 1.5f, harmonic: 0f);
            Mix(buffer, bubble, rng.Range(0.04f, 0.45f), rng.Range(0.12f, 0.3f));
        }
        return buffer;
    }

    // ---- hands and mouth ----

    private static float[] PickupPop()
    {
        var buffer = Buffer(0.17);
        var n = Noise(buffer.Length, 131);
        var hp = new HighPass(1500); var lp = new LowPass(4000);
        double phase = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            phase += 2 * Math.PI * (420 + 220 * MathF.Min(1f, t / 0.08f)) / Rate;
            buffer[i] = MathF.Sin((float)phase) * Env(t, 0.004f, 0.04f) * 0.7f + lp.Process(hp.Process(n[i])) * Env(t, 0.01f, 0.03f) * 0.3f;
        }
        return buffer;
    }

    private static float[] PutdownThud()
    {
        var buffer = Buffer(0.2);
        var n = Noise(buffer.Length, 141);
        var lp = new LowPass(900);
        double phase = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            phase += 2 * Math.PI * (300 - 110 * MathF.Min(1f, t / 0.1f)) / Rate;
            buffer[i] = MathF.Sin((float)phase) * Env(t, 0.003f, 0.05f) * 0.7f + lp.Process(n[i]) * Env(t, 0.002f, 0.025f) * 0.5f;
        }
        return buffer;
    }

    private static float[] EatCrunch()
    {
        var buffer = Buffer(0.6);
        var rng = new Rng(151);
        float[] Crunch(uint seed)
        {
            var c = Buffer(0.09);
            var n = Noise(c.Length, seed);
            var hp = new HighPass(700); var lp = new LowPass(3800);
            for (var i = 0; i < c.Length; i++)
            {
                var t = Time(i);
                var crackle = 0.6f + 0.4f * MathF.Sin(2 * MathF.PI * 190 * t);
                c[i] = lp.Process(hp.Process(n[i])) * Env(t, 0.002f, 0.022f) * crackle;
            }
            return c;
        }
        Mix(buffer, Crunch(152), 0.0, 1.0f);
        Mix(buffer, Crunch(153), 0.15 + rng.Range(0f, 0.02f), 0.8f);
        Mix(buffer, Crunch(154), 0.31, 0.55f);
        var swallow = Buffer(0.09);
        double phase = 0;
        for (var i = 0; i < swallow.Length; i++)
        {
            var t = Time(i);
            phase += 2 * Math.PI * (180 - 60 * t / 0.09f) / Rate;
            swallow[i] = MathF.Sin((float)phase) * MathF.Pow(MathF.Sin(MathF.PI * i / (swallow.Length - 1)), 2f);
        }
        Mix(buffer, swallow, 0.44, 0.22f);
        return buffer;
    }

    // ---- ui ----

    private static float[] Tone(double seconds, float freq, float attack, float decay)
    {
        var buffer = Buffer(seconds);
        for (var i = 0; i < buffer.Length; i++)
        {
            var t = Time(i);
            buffer[i] = (MathF.Sin(2 * MathF.PI * freq * t) + 0.2f * MathF.Sin(4 * MathF.PI * freq * t)) * Env(t, attack, decay);
        }
        return buffer;
    }

    private static float[] ConfirmTones()
    {
        var buffer = Buffer(0.3);
        Mix(buffer, Tone(0.15, 880, 0.002f, 0.05f), 0.0, 0.8f);
        Mix(buffer, Tone(0.2, 1320, 0.002f, 0.07f), 0.07, 1.0f);
        return buffer;
    }
}
