using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Audio;

/// <summary>
/// The audio bus layout: Master, then Ambience, Effects, UI, Voice and an empty Music bus (reserved: the founder has not chosen where music
/// comes from). Every other bus sends to Master. Master carries a hard limiter so nothing the children hear can pass its ceiling.
/// The layout is also shipped as game/assets/audio/default_bus_layout.tres for project.godot; EnsureLayout makes the same buses at run
/// time when that file is not set, and does nothing when they exist.
/// </summary>
public static class AudioBuses
{
    public const string Master = "Master";
    public const string Ambience = "Ambience";
    public const string Effects = "Effects";
    public const string UI = "UI";
    public const string Voice = "Voice";
    public const string Music = "Music";

    /// <summary>In layout order, Master first.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Master, Ambience, Effects, UI, Voice, Music };

    /// <summary>The loudest the Master limiter lets a sample go (dB below full scale).</summary>
    public const float LimiterCeilingDb = -3f;

    /// <summary>Creates any missing bus, sends it to Master and puts the limiter on Master. Safe to call any number of times.</summary>
    public static void EnsureLayout()
    {
        foreach (var name in All)
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            AudioServer.AddBus();
            var index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, name);
            AudioServer.SetBusSend(index, Master);
        }
        var master = AudioServer.GetBusIndex(Master);
        for (var i = 0; i < AudioServer.GetBusEffectCount(master); i++)
            if (AudioServer.GetBusEffect(master, i) is { } existing && IsLimiter(existing)) return;
        AudioServer.AddBusEffect(master, new AudioEffectHardLimiter { CeilingDb = LimiterCeilingDb });
    }

    public static bool IsLimiter(AudioEffect effect) => effect is AudioEffectHardLimiter;

    public static bool Has(string name) => AudioServer.GetBusIndex(name) >= 0;
}
