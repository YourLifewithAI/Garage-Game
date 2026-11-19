using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Audio;

/// <summary>
/// Volume settings for the buses, behind a small API for the settings screen (another lane's): Get and Set a bus's volume as a fraction
/// from 0 to 1, Mute and IsMuted, Reset, and Load and Save in a small config file in the user folder. Linear volume maps to decibels
/// with a curve (volume squared in power terms), and 0 is silence. Defaults are gentle: Master 0.8, the rest below it.
/// </summary>
public static class AudioSettings
{
    public const string DefaultPath = "user://audio.cfg";
    private const string Section = "volume";

    /// <summary>The loudest a bus may be set to: a setting of 1 is full on that bus, still under the Master limiter.</summary>
    public const float Max = 1f;

    private static readonly Dictionary<string, float> Defaults = new(StringComparer.Ordinal)
    {
        [AudioBuses.Master] = 0.8f, [AudioBuses.Ambience] = 0.7f, [AudioBuses.Effects] = 0.8f,
        [AudioBuses.UI] = 0.6f, [AudioBuses.Voice] = 1.0f, [AudioBuses.Music] = 0.6f,
    };

    private static readonly Dictionary<string, float> Values = new(Defaults, StringComparer.Ordinal);
    private static readonly HashSet<string> Muted = new(StringComparer.Ordinal);

    public static float DefaultOf(string bus) => Defaults.TryGetValue(bus, out var v) ? v : 1f;

    public static float Get(string bus) => Values.TryGetValue(bus, out var v) ? v : 1f;

    public static bool IsMuted(string bus) => Muted.Contains(bus);

    /// <summary>Sets a bus's volume (0 to 1; anything else is clamped, NaN is ignored) and applies it to the audio server.</summary>
    public static void Set(string bus, float volume)
    {
        if (!Defaults.ContainsKey(bus) || float.IsNaN(volume)) return;
        Values[bus] = Math.Clamp(volume, 0f, Max);
        Apply(bus);
    }

    public static void Mute(string bus, bool muted)
    {
        if (!Defaults.ContainsKey(bus)) return;
        if (muted) Muted.Add(bus); else Muted.Remove(bus);
        Apply(bus);
    }

    public static void Reset()
    {
        foreach (var (bus, value) in Defaults) { Values[bus] = value; Muted.Remove(bus); }
        ApplyAll();
    }

    /// <summary>The decibels a volume fraction stands for: a squared curve, so the slider's middle is a clear half, and 0 is silent.</summary>
    public static float ToDb(float volume) => !(volume > 0.0001f) ? -80f : Mathf.LinearToDb(volume * volume);

    public static void Apply(string bus)
    {
        var index = AudioServer.GetBusIndex(bus);
        if (index < 0) return;
        AudioServer.SetBusVolumeDb(index, ToDb(Get(bus)));
        AudioServer.SetBusMute(index, IsMuted(bus));
    }

    public static void ApplyAll()
    {
        foreach (var bus in AudioBuses.All) Apply(bus);
    }

    public static Error Save(string path = DefaultPath)
    {
        var file = new ConfigFile();
        foreach (var bus in AudioBuses.All)
        {
            var volume = Get(bus);
            file.SetValue(Section, bus, float.IsFinite(volume) ? Math.Clamp(volume, 0f, Max) : DefaultOf(bus));
            file.SetValue(Section, bus + "_muted", IsMuted(bus));
        }
        return file.Save(path);
    }

    /// <summary>
    /// Reads the file and applies the result. Everything in the file is untrusted: each value that is not a finite number from 0 to 1
    /// (NaN, infinity, out of range, a string, missing) takes its default, a mute flag that is not a real boolean is off, and unknown keys
    /// are ignored. A file that is not there leaves the current values; one that cannot be parsed gives the defaults.
    /// </summary>
    public static void Load(string path = DefaultPath)
    {
        var file = new ConfigFile();
        var error = file.Load(path);
        if (error is Error.FileNotFound or Error.FileCantOpen or Error.FileNoPermission) { ApplyAll(); return; }
        foreach (var bus in AudioBuses.All)
        {
            Values[bus] = DefaultOf(bus);
            Muted.Remove(bus);
            if (error != Error.Ok) continue;
            using var v = file.GetValue(Section, bus, Variant.From(DefaultOf(bus)));
            if (v.VariantType is Variant.Type.Float or Variant.Type.Int && v.AsDouble() is var d && double.IsFinite(d) && d >= 0 && d <= Max) Values[bus] = (float)d;
            using var m = file.GetValue(Section, bus + "_muted", Variant.From(false));
            if (m.VariantType == Variant.Type.Bool && m.AsBool()) Muted.Add(bus);
        }
        ApplyAll();
    }
}
