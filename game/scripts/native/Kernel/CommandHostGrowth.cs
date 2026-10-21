using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The single-player Glow trial's profile, separate from a room's undoable state. Only verified,
/// successful casts can advance it. Scene nodes and companion-supplied journal notes are never evidence.
/// Effects and their receipts still last for the session; restarting and casting again is a new use.
/// </summary>
public partial class CommandHost
{
    /// <summary>
    /// The team's expertise store (M3: &lt;team&gt;/expertise.json, one for every mode). The default team's until the boot
    /// (Saves/TeamBoot.cs) points it at the team it plays; boot fixtures redirect this before creating RoomWorld.
    /// </summary>
    public static string DefaultGlowProgressPath { get; set; } = Saves.TeamLayout.UserExpertisePath(Saves.TeamLayout.DefaultTeamId);
    public GlowProgress GlowProgress { get; private set; } = GlowProgress.Fresh;
    public string GlowProgressNotice { get; private set; } = "";
    private IGlowProgressStore? _glowProgressStore;
    private bool _glowProgressUnavailable;
    private string _glowMode = "creative";
    private Func<string, IGlowProgressStore>? _glowModeStoreFactory;
    private readonly Dictionary<string, (IGlowProgressStore? Store, GlowProgress Progress)> _glowModeProfiles = new(StringComparer.Ordinal);
    private sealed record GlowModeSelection(string Mode, IGlowProgressStore? Store, GlowProgress Progress);

    /// <summary>Trusted boot wiring. The original trial path is Creative; other modes keep separate progress.</summary>
    public void ConfigureGlowProgressModes(Func<string, IGlowProgressStore> storeForMode, string mode)
    {
        _glowModeStoreFactory = storeForMode;
        _glowMode = mode;
        ConfigureGlowProgress(storeForMode(mode));
    }

    private GlowModeSelection PrepareGlowMode(string mode)
    {
        if (mode == _glowMode)
        {
            CheckGlowProgressAvailable("glow");
            return new GlowModeSelection(mode, _glowProgressStore, GlowProgress);
        }
        try
        {
            if (_glowModeStoreFactory != null)
            {
                var store = _glowModeStoreFactory(mode);
                return new GlowModeSelection(mode, store, store.Load());
            }
            return _glowModeProfiles.TryGetValue(mode, out var cached)
                ? new GlowModeSelection(mode, cached.Store, cached.Progress)
                : new GlowModeSelection(mode, null, GlowProgress.Fresh);
        }
        catch (GlowProgressException)
        {
            throw new Refusal("not_ready", "That mode's Glow journal could not be loaded. Its saved progress has been kept.");
        }
    }

    private void CommitGlowMode(GlowModeSelection selected)
    {
        if (selected.Mode != _glowMode) EndAllEffects();
        _glowModeProfiles[_glowMode] = (_glowProgressStore, GlowProgress);
        _glowMode = selected.Mode;
        _glowProgressStore = selected.Store;
        GlowProgress = selected.Progress;
        _glowProgressUnavailable = false;
        GlowProgressNotice = "";
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.SetGlowAudio((float)GlowProgress.SoundVolume, GlowProgress.SoundMuted);
        // The pool's maximum follows the expertise learned.
        ResolveVitalsRules();
    }

    /// <summary>Trusted boot/test wiring only. No command or companion query can supply a profile.</summary>
    public void ConfigureGlowProgress(IGlowProgressStore store)
    {
        if (_effects.Count != 0) throw new InvalidOperationException("Configure growth before starting effects.");
        _glowProgressStore = store;
        try
        {
            var loaded = store.Load();
            GlowProgress = loaded;
            _glowProgressUnavailable = false;
            GlowProgressNotice = "";
            ResolveVitalsRules();
        }
        catch (GlowProgressException)
        {
            _glowProgressUnavailable = true;
            GlowProgressNotice = "The Glow journal could not be loaded. Your saved progress has been kept; Glow is waiting for it to be recovered.";
        }
    }

    /// <summary>The learned allowance never exceeds either the pack or the engine's room limit.</summary>
    public int EffectiveEffectLimit(string capability)
    {
        var ability = Rules?.Ability(capability);
        if (ability == null) return 0;
        return Math.Min(Math.Min(ability.MaxActive, EffectLimit), capability == "glow" ? GlowProgress.ActiveLightAllowance : ability.MaxActive);
    }

    private void CheckGlowProgressAvailable(string capability)
    {
        if (capability == "glow" && _glowProgressUnavailable)
            throw new Refusal("not_ready", GlowProgressNotice);
    }

    /// <summary>
    /// Called after the look accepts a cast and before the effect/receipt is published. A write failure
    /// leaves this state untouched; the caller removes the temporary light before returning a refusal.
    /// The caller supplies only variants proved by the room, physics and accepted look.
    /// </summary>
    private void RecordGlowCast(string variant, bool constellation)
    {
        var discovery = variant is "lantern" or "torch" or "bioluminesce" or "skylight" ? variant : null;
        var candidate = GlowProgress.AfterObservedCast(discovery, constellation);
        SaveGlowProgress(candidate);
    }

    private void SaveGlowProgress(GlowProgress candidate)
    {
        try { _glowProgressStore?.Save(candidate); }
        catch (GlowProgressException)
        {
            GlowProgressNotice = "The Glow journal could not be saved. This change was cancelled; your earlier progress is safe. Try again once saving is available.";
            throw new Refusal("not_ready", GlowProgressNotice, retryable: true);
        }
        PublishProgress(candidate);
    }

    /// <summary>
    /// The candidate is saved: it becomes the team's progress. Every expertise it adds, in any ability, is a reward with a pace line
    /// of its own (the total counted), and the pool's maximum may rise with the total (a pace line of its own).
    /// </summary>
    private void PublishProgress(GlowProgress candidate)
    {
        var previous = GlowProgress;
        var before = previous.ExpertiseCount;
        GlowProgress = candidate;
        GlowProgressNotice = "";
        if (candidate.ExpertiseCount <= before) return;
        // The expertise announcement (Fire's camp): each one newly learned and saved, for the HUD's chime and card (CommandHostCamps).
        NoteLearned(previous, candidate);
        for (var learned = before + 1; learned <= candidate.ExpertiseCount; learned++) NotePace("expertise", learned);
        ResolveVitalsRules(pace: true);
    }

    /// <summary>
    /// Saves a candidate for an expertise that is a reward and no precondition (Bubbles, Fire, the Bubble's events): when the
    /// journal cannot be saved the cast or the fire it came from goes on, nothing is learned yet, and the notice says so (the same
    /// expertise comes again at the next chance). Glow's own casts are different: its journal cancels the cast (SaveGlowProgress).
    /// Returns whether the candidate was saved and published.
    /// </summary>
    private bool TrySaveExpertise(GlowProgress candidate)
    {
        // A store that could not be loaded is waiting to be recovered (its notice says so): nothing is written to it.
        if (_glowProgressUnavailable) return false;
        try { _glowProgressStore?.Save(candidate); }
        catch (GlowProgressException)
        {
            GlowProgressNotice = "The journal could not be saved, so what the Gubble just learned was not kept. Your earlier progress is safe; it can be learned again once saving is available.";
            return false;
        }
        PublishProgress(candidate);
        return true;
    }

    public JsonObject PlayerGlowTouch(string touch) => PlayerCommand("glow.configure", new JsonObject { ["touch"] = touch });
    public JsonObject PlayerGlowAudio(float volume, bool muted) => PlayerCommand("glow.configure", new JsonObject { ["sound_volume"] = volume, ["muted"] = muted });

    private JsonObject ConfigureGlow(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player chooses Glow's touch and sound settings.");
        CheckGlowProgressAvailable("glow");
        var touch = args.TryGetProperty("touch", out var t) ? t.GetString()! : GlowProgress.SelectedTouch;
        var volume = args.TryGetProperty("sound_volume", out var v) ? v.GetDouble() : GlowProgress.SoundVolume;
        var muted = args.TryGetProperty("muted", out var m) ? m.GetBoolean() : GlowProgress.SoundMuted;
        var candidate = GlowProgress.WithConfiguration(touch, volume, muted);
        var data = new JsonObject { ["touch"] = touch, ["sound_volume"] = volume, ["muted"] = muted };
        if (preview)
        {
            var result = Previewed("glow.configure", principal, actionId);
            result["data"] = data;
            return result;
        }
        SaveGlowProgress(candidate);
        if (LookHook != null && IsInstanceValid(LookHook)) LookHook.SetGlowAudio((float)volume, muted);
        return Transient("glow.configure", principal, actionId, fingerprint, new JsonArray(), data);
    }

    private static void CheckGlowConfigureArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new Refusal("request_invalid", "Glow settings are an object.", "$.args");
        foreach (var field in args.EnumerateObject())
            if (field.Name is not ("touch" or "sound_volume" or "muted"))
                throw new Refusal("field_unknown", "This argument is not part of Glow's settings.", "$.args." + KernelJson.DisplayText(field.Name, 64));
        if (!args.EnumerateObject().Any()) throw new Refusal("request_invalid", "Choose a touch or sound setting.", "$.args");
        if (args.TryGetProperty("touch", out var touch) && (touch.ValueKind != JsonValueKind.String || !GlowProgress.Touches.Contains(touch.GetString()!, StringComparer.Ordinal)))
            throw new Refusal("request_invalid", "Choose an available Glow touch.", "$.args.touch");
        if (args.TryGetProperty("sound_volume", out var volume) && (volume.ValueKind != JsonValueKind.Number || !volume.TryGetDouble(out var level) || !double.IsFinite(level) || level is < 0 or > 1))
            throw new Refusal("request_invalid", "Sound volume is between zero and one.", "$.args.sound_volume");
        if (args.TryGetProperty("muted", out var muted) && muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new Refusal("request_invalid", "Muted is true or false.", "$.args.muted");
    }
}
