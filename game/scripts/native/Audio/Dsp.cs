using System;

namespace GarageGame.Native.Audio;

/// <summary>
/// The small signal toolbox the game's sounds are made from. Pure C# on arrays: no Godot types, so it runs on a worker thread and in any
/// test. Everything is deterministic: the same recipe gives the same samples on every run (a seeded xorshift, no clock, no global state).
/// </summary>
public static class Dsp
{
    public const int Rate = 22050;

    /// <summary>A seeded xorshift32 generator.</summary>
    public sealed class Rng
    {
        private uint _state;
        public Rng(uint seed) { _state = seed == 0 ? 0x9E3779B9u : seed; }
        public uint NextU()
        {
            var x = _state;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _state = x;
            return x;
        }
        /// <summary>0 up to but not including 1.</summary>
        public float Next01() => (NextU() >> 8) * (1f / 16777216f);
        /// <summary>-1 up to but not including 1.</summary>
        public float Signed() => Next01() * 2f - 1f;
        public float Range(float low, float high) => low + (high - low) * Next01();
    }

    /// <summary>One-pole low-pass filter.</summary>
    public struct LowPass
    {
        private float _a, _y;
        public LowPass(float cutoffHz) { _a = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / Rate); _y = 0; }
        public void Set(float cutoffHz) => _a = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / Rate);
        public float Process(float x) { _y += _a * (x - _y); return _y; }
    }

    /// <summary>One-pole high-pass filter.</summary>
    public struct HighPass
    {
        private LowPass _low;
        public HighPass(float cutoffHz) { _low = new LowPass(cutoffHz); }
        public void Set(float cutoffHz) => _low.Set(cutoffHz);
        public float Process(float x) => x - _low.Process(x);
    }

    public static float[] Buffer(double seconds) => new float[(int)Math.Round(seconds * Rate)];

    public static float Time(int index) => (float)index / Rate;

    /// <summary>
    /// Runs a stateful per-sample function twice over a loop and keeps the second pass, so filter state at the end of the buffer
    /// is what the start of the buffer was made from: the loop's seam is as smooth as any other point in it.
    /// </summary>
    public static void Circular(float[] buffer, Func<int, float> sample)
    {
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < buffer.Length; i++)
            {
                var y = sample(i);
                if (pass == 1) buffer[i] = y;
            }
    }

    /// <summary>Scales the buffer so its largest sample is exactly peak (a fraction of full scale, below 1).</summary>
    public static float[] Normalize(float[] buffer, float peak)
    {
        var max = 0f;
        foreach (var v in buffer) max = MathF.Max(max, MathF.Abs(v));
        if (max <= 0) return buffer;
        var gain = peak / max;
        for (var i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        return buffer;
    }

    /// <summary>Takes out the mean (a loop with an offset thumps where it repeats on some speakers).</summary>
    public static void RemoveDc(float[] buffer)
    {
        double sum = 0;
        foreach (var v in buffer) sum += v;
        var mean = (float)(sum / buffer.Length);
        for (var i = 0; i < buffer.Length; i++) buffer[i] -= mean;
    }

    /// <summary>A short rise and fall so a one-shot starts and ends at silence, never on a click.</summary>
    public static void Fade(float[] buffer, float inSeconds = 0.004f, float outSeconds = 0.012f)
    {
        var fadeIn = Math.Max(1, (int)(inSeconds * Rate));
        var fadeOut = Math.Max(1, (int)(outSeconds * Rate));
        for (var i = 0; i < buffer.Length; i++)
        {
            var g = 1f;
            if (i < fadeIn) g = (float)i / fadeIn;
            var left = buffer.Length - 1 - i;
            if (left < fadeOut) g = MathF.Min(g, (float)left / fadeOut);
            buffer[i] *= g;
        }
    }

    /// <summary>Adds src into dst starting at start seconds, wrapping around the end when wrap is set (for loops).</summary>
    public static void Mix(float[] dst, float[] src, double start, float gain = 1f, bool wrap = false)
    {
        var offset = (int)Math.Round(start * Rate);
        for (var i = 0; i < src.Length; i++)
        {
            var at = offset + i;
            if (at >= dst.Length) { if (!wrap) break; at %= dst.Length; }
            dst[at] += src[i] * gain;
        }
    }

    /// <summary>A smooth 0..1 ramp.</summary>
    public static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>16-bit little-endian mono PCM of the buffer, clamped.</summary>
    public static byte[] ToPcm16(float[] buffer)
    {
        var data = new byte[buffer.Length * 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            var s = (short)Math.Clamp((int)MathF.Round(buffer[i] * 32767f), -32767, 32767);
            data[i * 2] = (byte)(s & 255);
            data[i * 2 + 1] = (byte)((s >> 8) & 255);
        }
        return data;
    }

    /// <summary>A mono 16-bit WAV file's bytes (44-byte header), for the preview renders.</summary>
    public static byte[] ToWav(float[] buffer)
    {
        var pcm = ToPcm16(buffer);
        using var stream = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(stream))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + pcm.Length);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return stream.ToArray();
    }
}
