using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using static GarageGame.Native.Audio.Dsp;

namespace GarageGame.Native.Audio;

/// <summary>One sound the director started: its id, the bus it plays on and when (director seconds).</summary>
public readonly record struct PlayedSound(string Id, string Bus, double AtS);

/// <summary>
/// The room's sound. One director: <c>AudioDirector.Attach(world)</c> after the command host is attached. It listens and edits nothing:
/// CommandHost.Handled (picking up, putting down), CommandHost.FoodSound (eating), the weather, clock, season, campfires and the
/// bodies' public state through an <see cref="IAudioSource"/>, and every button that appears in the scene tree (UI ticks).
/// Every sound is made from code on a worker thread at start (SoundBank); the main thread only wraps finished samples in streams,
/// three a frame, and sets player volumes. Nothing is loaded from a file. Gentle by design: the loops are quiet, volumes ease
/// over a second and a half, steps and ticks are rate-limited, and the Master bus has a limiter.
/// </summary>
public partial class AudioDirector : Node
{
    /// <summary>Seconds a loop takes to reach a new level.</summary>
    public const float EaseS = 1.5f;
    public const int EffectVoices = 8;
    public const int BirdVoices = 3;
    /// <summary>Streams wrapped per frame once the worker has made them.</summary>
    public const int StreamsPerFrame = 3;
    public const float MinUiGapS = 0.05f;
    public const float WalkStrideM = 0.11f;
    public const float RunStrideM = 0.17f;
    public const float WadeStrideM = 0.09f;
    public const float SwimGapS = 1.0f;
    public const float MinStepSpeedMps = 0.06f;
    public const float RunSpeedMps = 0.6f;
    /// <summary>Within this distance (m) of a lit campfire its fire is heard.</summary>
    public const float FireHearingM = 6f;

    private static readonly string[] LoopIds = { Sounds.WindSoft, Sounds.WindGust, Sounds.Sea, Sounds.Rain, Sounds.Insects };
    private readonly float[] _targets = new float[5];
    private readonly List<Loop> _loopList = new();
    private readonly List<Node> _watched = new();
    private readonly System.Threading.CancellationTokenSource _cancel = new();

    private sealed class Loop
    {
        public string Id = "";
        public AudioStreamPlayer? Player;
        public float Level;
        public float Target;
    }

    private IAudioSource _source = null!;
    private CommandHost? _host;
    private readonly ConcurrentDictionary<string, byte[]> _pcm = new();
    private readonly Dictionary<string, AudioStreamWav> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundRecipe> _recipes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.Ordinal);
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly List<AudioStreamPlayer3D> _birdVoices = new();
    private AudioStreamPlayer3D? _fire;
    private string? _fireId;
    private float _fireLevel;
    private int _nextVoice, _nextBird;
    private Rng _rng = new(2026);
    private Task? _worker;
    private double _clock;
    private double _lastUiS = -1;
    private float _stride;
    private float _swimClock;
    private int _stepVariant;
    private FeetWater _lastWater = FeetWater.Dry;
    private bool _lastOnFloor = true;
    private float _airS;
    private bool _seen;
    private float _birdClock = 3f;
    private readonly List<BaseButton> _wired = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<PlayedSound> _log = new();

    /// <summary>When true (the game), the director updates itself every frame. A test turns it off and calls <see cref="Update"/>.</summary>
    public bool SelfUpdating { get; set; } = true;
    /// <summary>
    /// Whether sounds are really started on the audio server. The director decides, logs and eases exactly the same either way. The game
    /// turns it off on a headless run (the test runners and servers: nobody can hear, and an audio playback still running when the
    /// engine quits is reported by Godot as leaked ObjectDB instances, which fails every test that wants a silent stderr).
    /// </summary>
    public bool PlayAudio { get; set; } = true;
    /// <summary>True once every sound is made and wrapped in a stream.</summary>
    public bool StreamsReady { get; private set; }
    /// <summary>The most recent sounds started (up to 256), oldest first.</summary>
    public IReadOnlyList<PlayedSound> Log => _log;
    /// <summary>Seconds the director has run, counted from its own process delta (it keeps counting while the tree is paused).</summary>
    public double ClockS => _clock;
    /// <summary>Milliseconds of main thread the Attach call took (measured, for the report; the sounds are made on a worker).</summary>
    public double AttachMs { get; private set; }
    public int WiredButtons => _wired.Count;
    public int WatchedContainers => _watched.Count;
    public int PlayedCount(string id) => _counts.TryGetValue(id, out var n) ? n : 0;
    public int PlayedPrefix(string prefix) => _counts.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(p => p.Value);
    public event Action<PlayedSound>? Played;

    /// <summary>The level (0 to 1) a loop is easing to, and where it is now.</summary>
    public float LoopTarget(string id) => _loops.TryGetValue(id, out var l) ? l.Target : 0;
    public float LoopLevel(string id) => _loops.TryGetValue(id, out var l) ? l.Level : 0;
    public bool LoopPlaying(string id) => _loops.TryGetValue(id, out var l) && l.Player is { Playing: true };
    public bool FirePlaying => _fire is { Playing: true };
    public string? FireAt => _fireId;

    /// <summary>The one entry point. Call it once the room's command host is attached (RoomWorld, after CommandHost.Attach).</summary>
    public static AudioDirector Attach(RoomWorld world)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var director = new AudioDirector { Name = "AudioDirector" };
        world.AddChild(director);
        var host = CommandHost.Of(world);
        director.PlayAudio = DisplayServer.GetName() != "headless";
        director.Start(new RoomAudioSource(world, host), host, loadSettings: true);
        director.AttachMs = timer.Elapsed.TotalMilliseconds;
        GD.Print($"ROOM_AUDIO_ATTACH {director.AttachMs:0.00} ms of main thread ({director.WatchedContainers} UI containers watched, {director.WiredButtons} buttons wired, audio {(director.PlayAudio ? "on" : "off: headless")})");
        return director;
    }

    /// <summary>For tests and tools: a director on any source, in the given parent, updated by hand.</summary>
    public static AudioDirector Create(Node parent, IAudioSource source, CommandHost? host = null, bool loadSettings = false, uint seed = 2026)
    {
        var director = new AudioDirector { Name = "AudioDirector", SelfUpdating = false, _rng = new Rng(seed) };
        parent.AddChild(director);
        director.Start(source, host, loadSettings);
        return director;
    }

    private void Start(IAudioSource source, CommandHost? host, bool loadSettings)
    {
        _source = source;
        // The Esc menu and the Gubble wheel pause the tree: the director must keep running through it, to fade the world's sounds
        // and to keep the menu's own ticks working. Its players inherit this.
        ProcessMode = ProcessModeEnum.Always;
        AudioBuses.EnsureLayout();
        if (loadSettings) AudioSettings.Load(); else AudioSettings.ApplyAll();
        foreach (var recipe in SoundBank.All) _recipes[recipe.Id] = recipe;
        for (var i = 0; i < EffectVoices; i++)
        {
            var voice = new AudioStreamPlayer { Name = "Voice" + i };
            AddChild(voice);
            _voices.Add(voice);
        }
        for (var i = 0; i < BirdVoices; i++)
        {
            var voice = new AudioStreamPlayer3D { Name = "Bird" + i, Bus = AudioBuses.Ambience, UnitSize = 3f, MaxDistance = 40f };
            AddChild(voice);
            _birdVoices.Add(voice);
        }
        foreach (var id in LoopIds)
        {
            var player = new AudioStreamPlayer { Name = "Loop_" + id, Bus = AudioBuses.Ambience, VolumeDb = -80 };
            AddChild(player);
            var loop = new Loop { Id = id, Player = player };
            _loops[id] = loop;
            _loopList.Add(loop);
        }
        _fire = new AudioStreamPlayer3D { Name = "Loop_fire", Bus = AudioBuses.Effects, UnitSize = 1.5f, MaxDistance = FireHearingM, VolumeDb = -80 };
        AddChild(_fire);
        if (host != null) Bind(host);
        // Synthesis runs off the main thread; nothing waits for it. The sounds fade in as they are wrapped.
        var token = _cancel.Token;
        _worker = Task.Run(() => MakeAll(token));
        // Buttons live in UI trees, never in the 3D world: only the window's and the world's own children are watched, and below them
        // only Control and CanvasLayer subtrees (WatchUi). The thousands of bodies, plants and lights of an island are never walked
        // and never reported to this script.
        WatchContainer(GetTree().Root);
        if (GetParent() is { } world) WatchContainer(world);
    }

    private void MakeAll(System.Threading.CancellationToken token)
    {
        try
        {
            foreach (var recipe in SoundBank.All)
            {
                if (token.IsCancellationRequested) return;
                _pcm[recipe.Id] = ToPcm16(SoundBank.Render(recipe));
            }
        }
        catch (Exception error) { GD.PushError("AudioDirector synthesis failed: " + error); }
    }

    /// <summary>Waits for the worker and wraps every stream now. For tests and tools; the game never calls it.</summary>
    public void LoadAllNow()
    {
        _worker?.Wait();
        while (WrapStreams(int.MaxValue) > 0) { }
    }

    private int WrapStreams(int most)
    {
        var made = 0;
        foreach (var recipe in SoundBank.All)
        {
            if (made >= most) break;
            if (_streams.ContainsKey(recipe.Id) || !_pcm.TryGetValue(recipe.Id, out var data)) continue;
            var stream = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
            if (recipe.Loop)
            {
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                stream.LoopBegin = 0;
                stream.LoopEnd = data.Length / 2;
            }
            _streams[recipe.Id] = stream;
            made++;
        }
        StreamsReady = _streams.Count == SoundBank.All.Count;
        return made;
    }

    // ---- hooks ----

    /// <summary>Listens to a command host: successful, real, player commands make their sounds; previews, replays, refusals and the companion's do not.</summary>
    public void Bind(CommandHost host)
    {
        if (_host != null) { _host.Handled -= OnHandled; _host.FoodSound -= OnFood; }
        _host = host;
        host.Handled += OnHandled;
        host.FoodSound += OnFood;
    }

    /// <summary>How many messages the bound host has answered since the director bound to it (a test reads it to know the hook is live).</summary>
    public int HandledSeen { get; private set; }

    public void OnHandled(CommandHandled handled)
    {
        HandledSeen++;
        if (!handled.Ok || handled.Preview || handled.Replayed || handled.Principal != CommandHost.PlayerPrincipal) return;
        switch (handled.Op)
        {
            case "item.pickup" or "item.gather" or "inventory.take": PlayOnce(Sounds.Pickup, -2f); break;
            case "inventory.drop" or "inventory.store": PlayOnce(Sounds.Putdown, -2f); break;
        }
    }

    public void OnFood(string kind) => PlayOnce(Sounds.Eat, -2f);

    /// <summary>A UI sound (tick, hover or confirm), rate-limited so a sweep over many buttons is a ripple and not a buzz.</summary>
    public void PlayUi(string id)
    {
        if (_lastUiS >= 0 && _clock - _lastUiS < MinUiGapS) return;
        _lastUiS = _clock;
        PlayOnce(id, 0f, jitter: false);
    }

    /// <summary>Watches a node's children as they enter, and visits the ones already there.</summary>
    private void WatchContainer(Node container)
    {
        if (container.HasMeta("audio_watched")) return;
        container.SetMeta("audio_watched", true);
        container.ChildEnteredTree += OnChildEntered;
        _watched.Add(container);
        var count = container.GetChildCount();
        for (var i = 0; i < count; i++) Visit(container.GetChild(i));
    }

    private void OnChildEntered(Node child) => Visit(child);

    /// <summary>A button is wired; a UI subtree (anything that is not 3D) is walked and watched; a 3D subtree is skipped whole.</summary>
    private void Visit(Node node)
    {
        if (node is BaseButton button) Wire(button);
        if (node is Node3D) return;
        WatchContainer(node);
    }

    private void Wire(BaseButton button)
    {
        if (button.HasMeta("audio_wired")) return;
        button.SetMeta("audio_wired", true);
        button.Pressed += OnButtonPressed;
        button.MouseEntered += OnButtonHover;
        _wired.Add(button);
    }

    private double _pruneClock;

    /// <summary>Drops the buttons and containers that have been freed (the journal rebuilds its buttons all the time).</summary>
    private void Prune()
    {
        _wired.RemoveAll(IsGone);
        _watched.RemoveAll(IsGone);
    }

    private static bool IsGone(Node node) => !IsInstanceValid(node);

    private void OnButtonPressed() => PlayUi(Sounds.UiTick);
    private void OnButtonHover() => PlayUi(Sounds.UiHover);

    // ---- the frame ----

    public override void _Process(double delta)
    {
        if (SelfUpdating) Update(delta);
    }

    /// <summary>Reads the world once and moves every sound toward what it should be. Called every frame by the game, by hand in tests.</summary>
    public void Update(double delta)
    {
        var dt = (float)Math.Max(0, delta);
        _clock += dt;
        if (!StreamsReady) WrapStreams(StreamsPerFrame);
        if ((_pruneClock += dt) > 5) { _pruneClock = 0; Prune(); }
        var s = _source.Read();
        UpdateAmbience(s, dt);
        UpdateFire(s, dt);
        UpdateFeet(s, dt);
        UpdateBirds(s, dt);
    }

    private void UpdateAmbience(AudioSnapshot s, float dt)
    {
        var open = s.Outdoors ? 1f : 0.15f;
        var gust = Smooth(0.14f, 0.30f, s.WindMps);
        var winter = s.Season == "winter";
        var night = 1f - Smooth(0.15f, 0.5f, s.Daylight);
        var rain = MathF.Pow(Math.Clamp(s.Rain01, 0f, 1f), 0.7f) * (s.Outdoors ? 1f : 0.35f);
        // In LoopIds order: wind soft, wind gust, sea, rain, insects.
        _targets[0] = 0.85f * (1f - 0.6f * gust) * open;
        _targets[1] = 0.9f * gust * open;
        _targets[2] = 0.9f * Math.Clamp(s.SeaNear01, 0f, 1f) * (s.Outdoors ? 1f : 0.3f);
        _targets[3] = rain;
        _targets[4] = 0.8f * night * (winter ? 0.25f : 1f) * (1f - 0.7f * Smooth(0.3f, 0.7f, s.Rain01)) * (s.Outdoors ? 1f : 0.2f);
        var ease = 1f - MathF.Exp(-dt / EaseS);
        for (var i = 0; i < _loopList.Count; i++)
        {
            var loop = _loopList[i];
            // Paused (the Esc menu, the Gubble wheel): the world's sounds ease out, and back in on resume.
            loop.Target = s.Paused ? 0 : Math.Clamp(_targets[i], 0f, 1f);
            loop.Level += (loop.Target - loop.Level) * ease;
            Drive(loop.Player, loop.Id, loop.Level);
        }
    }

    private void Drive(AudioStreamPlayer? player, string id, float level)
    {
        if (player == null) return;
        if (level < 0.003f) { if (player.Playing) player.Stop(); return; }
        if (!_streams.TryGetValue(id, out var stream)) return;
        player.VolumeDb = Mathf.LinearToDb(level);
        if (PlayAudio && !player.Playing) { player.Stream = stream; player.Play((float)(_rng.Next01() * stream.GetLength())); }
    }

    private void UpdateFire(AudioSnapshot s, float dt)
    {
        if (_fire == null) return;
        LitFire? nearest = null;
        var best = float.MaxValue;
        foreach (var fire in s.Fires)
        {
            var d = fire.Position.DistanceTo(s.PlayerPosition);
            if (d < best) { best = d; nearest = fire; }
        }
        var target = !s.Paused && nearest != null && best <= FireHearingM ? 1f : 0f;
        _fireId = target > 0 ? nearest!.Value.Id : null;
        _fireLevel += (target - _fireLevel) * (1f - MathF.Exp(-dt / EaseS));
        if (nearest != null && target > 0) _fire.GlobalPosition = nearest.Value.Position + Vector3.Up * 0.05f;
        if (_fireLevel < 0.003f) { if (_fire.Playing) _fire.Stop(); return; }
        if (!_streams.TryGetValue(Sounds.Fire, out var stream)) return;
        _fire.VolumeDb = Mathf.LinearToDb(_fireLevel);
        if (PlayAudio && !_fire.Playing) { _fire.Stream = stream; _fire.Play((float)(_rng.Next01() * stream.GetLength())); }
    }

    private void UpdateFeet(AudioSnapshot s, float dt)
    {
        if (!_seen) { _lastWater = s.Water; _lastOnFloor = s.OnFloor; _seen = true; }
        if (s.Paused) return;
        // Into the water, a splash; out of it nothing (the feet are wet already).
        if (_lastWater == FeetWater.Dry && s.Water != FeetWater.Dry) PlayOnce(Sounds.Splash, -2f);
        _lastWater = s.Water;
        if (!s.OnFloor) _airS += dt;
        else
        {
            // A landing is a firmer step, once the body has been in the air long enough to have jumped.
            if (!_lastOnFloor && _airS > 0.15f && s.Water == FeetWater.Dry) Step(s, 1f);
            _airS = 0;
        }
        _lastOnFloor = s.OnFloor;
        if (s.Water == FeetWater.Swimming)
        {
            _swimClock += s.PlayerSpeedMps > 0.03f ? dt : 0;
            if (_swimClock >= SwimGapS) { _swimClock = 0; PlayOnce(Sounds.Swim, -3f); }
            return;
        }
        _swimClock = SwimGapS * 0.5f;
        if (!s.OnFloor || s.PlayerSpeedMps < MinStepSpeedMps)
        {
            if (s.PlayerSpeedMps < MinStepSpeedMps) _stride = 0;
            return;
        }
        _stride += s.PlayerSpeedMps * dt;
        var stride = s.Water == FeetWater.Wading ? WadeStrideM : s.PlayerSpeedMps > RunSpeedMps ? RunStrideM : WalkStrideM;
        if (_stride < stride) return;
        _stride -= stride;
        if (s.Water == FeetWater.Wading) PlayOnce(Sounds.Wade, -3f);
        else Step(s, s.PlayerSpeedMps > RunSpeedMps ? 2f : 0f);
    }

    private void Step(AudioSnapshot s, float louderDb)
    {
        var ground = s.GroundNow();
        if (!Sounds.Grounds.Contains(ground)) ground = "grass";
        _stepVariant ^= 1;
        PlayOnce(Sounds.Step(ground, _stepVariant), -3f + louderDb);
    }

    private void UpdateBirds(AudioSnapshot s, float dt)
    {
        if (s.Paused || !s.Outdoors || s.Daylight < 0.35f || s.Rain01 > 0.35f) return;
        _birdClock -= dt;
        if (_birdClock > 0) return;
        var winter = s.Season == "winter";
        _birdClock = _rng.Range(5f, 15f) * (winter ? 2.5f : 1f) / (0.5f + 0.5f * s.Daylight);
        var id = Sounds.Birds[(int)(_rng.NextU() % Sounds.Birds.Length)];
        if (!_streams.TryGetValue(id, out var stream)) return;
        var voice = _birdVoices[_nextBird++ % _birdVoices.Count];
        var angle = _rng.Range(0f, MathF.Tau);
        var distance = _rng.Range(3f, 8f);
        voice.GlobalPosition = s.PlayerPosition + new Vector3(MathF.Cos(angle) * distance, _rng.Range(1f, 3f), MathF.Sin(angle) * distance);
        voice.Stream = stream;
        voice.VolumeDb = _rng.Range(-4f, -1f);
        voice.PitchScale = _rng.Range(0.9f, 1.15f);
        if (PlayAudio) voice.Play();
        Record(id, AudioBuses.Ambience);
    }

    /// <summary>Starts a one-shot on a free voice (the oldest is taken when all are busy), with a little volume and pitch variety.</summary>
    private void PlayOnce(string id, float volumeDb, bool jitter = true)
    {
        if (!_streams.TryGetValue(id, out var stream) || !_recipes.TryGetValue(id, out var recipe)) return;
        var voice = _voices.FirstOrDefault(v => !v.Playing) ?? _voices[_nextVoice++ % _voices.Count];
        voice.Stream = stream;
        voice.Bus = recipe.Bus;
        voice.VolumeDb = volumeDb + (jitter ? _rng.Range(-1.5f, 1.5f) : 0);
        voice.PitchScale = jitter ? _rng.Range(0.95f, 1.05f) : 1f;
        if (PlayAudio) voice.Play();
        Record(id, recipe.Bus);
    }

    private void Record(string id, string bus)
    {
        var played = new PlayedSound(id, bus, _clock);
        _counts[id] = PlayedCount(id) + 1;
        _log.Add(played);
        if (_log.Count > 256) _log.RemoveAt(0);
        Played?.Invoke(played);
    }

    public override void _ExitTree()
    {
        if (_host != null) { _host.Handled -= OnHandled; _host.FoodSound -= OnFood; }
        // A game that quits before synthesis is done: the worker stops at its next sound and nothing waits for it.
        _cancel.Cancel();
        foreach (var node in _watched)
            if (IsInstanceValid(node)) { node.ChildEnteredTree -= OnChildEntered; node.RemoveMeta("audio_watched"); }
        _watched.Clear();
        foreach (var button in _wired)
            if (IsInstanceValid(button)) { button.Pressed -= OnButtonPressed; button.MouseEntered -= OnButtonHover; button.RemoveMeta("audio_wired"); }
        _wired.Clear();
        // Players let go of their streams before the streams are disposed. Stopping only asks: the audio server's mixer drops a
        // playback (and with it the playback's hold on its stream) on its next passes, and an engine that quits first reports the
        // stream and the playback as leaked ObjectDB instances. So when anything was playing, give the mixer a few passes.
        var stopped = false;
        void Release(AudioStreamPlayer? player) { if (player == null) return; stopped |= player.Playing; player.Stop(); player.Stream = null; }
        foreach (var player in _voices) Release(player);
        foreach (var loop in _loopList) Release(loop.Player);
        foreach (var voice in _birdVoices) { stopped |= voice.Playing; voice.Stop(); voice.Stream = null; }
        if (_fire != null) { stopped |= _fire.Playing; _fire.Stop(); _fire.Stream = null; }
        if (stopped) System.Threading.Thread.Sleep(MixerDrainMs);
        foreach (var stream in _streams.Values) stream.Dispose();
        _streams.Clear();
        (_source as IDisposable)?.Dispose();
    }

    /// <summary>How long to let the mixer drop stopped playbacks when the director leaves the tree.</summary>
    public const int MixerDrainMs = 150;
}
