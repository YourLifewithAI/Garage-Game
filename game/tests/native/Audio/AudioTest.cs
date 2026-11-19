using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GarageGame.Native;
using GarageGame.Native.Audio;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Audio;

/// <summary>
/// The Sound lane (docs/audio/README.md): the bus layout and the volume API; every sound made in code (deterministic, loops without a click,
/// quiet, silent at its ends); and the director's hooks, each firing on its event and silent otherwise, on a hand-filled world and on a
/// real command host. "-- --preview=DIR" writes every sound as a WAV into DIR (never committed) and quits.
/// </summary>
public partial class AudioTest : Node3D
{
    private int _checks;
    private int _failures;

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failures++;
        GD.PrintErr("FAIL: " + what);
    }

    private sealed class FakeSource : IAudioSource
    {
        public AudioSnapshot S = new();
        public AudioSnapshot Read() => S;
    }

    public override async void _Ready()
    {
        try
        {
            var preview = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--preview=", StringComparison.Ordinal))?["--preview=".Length..];
            if (preview != null)
            {
                RenderPreview(preview);
                GetTree().Quit(0);
                return;
            }
            TestBuses();
            TestSettings();
            TestSounds();
            await TestDirector();
            await TestRealHost();
            GD.Print($"NATIVE_KERNEL_AUDIO: {_checks - _failures}/{_checks} checks passed; the bus layout and volume settings, {SoundBank.All.Count} sounds made in code (deterministic, quiet, loops seamless), and the director's ambience, fire, feet, water, hands, eating, birds and UI hooks");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError("Audio test exception: " + error);
            GetTree().Quit(1);
        }
    }

    // ---- previews ----

    private static void RenderPreview(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (var recipe in SoundBank.All)
        {
            var samples = SoundBank.Render(recipe);
            if (recipe.Loop)
            {
                // A loop is written three times over so the ear can judge the seam.
                var three = new float[samples.Length * 3];
                for (var i = 0; i < three.Length; i++) three[i] = samples[i % samples.Length];
                samples = three;
            }
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, recipe.Id + ".wav"), Dsp.ToWav(samples));
        }
        GD.Print($"AUDIO_PREVIEW: {SoundBank.All.Count} files in {directory}");
    }

    // ---- buses and settings ----

    private void TestBuses()
    {
        AudioBuses.EnsureLayout();
        AudioBuses.EnsureLayout();
        var names = Enumerable.Range(0, AudioServer.BusCount).Select(AudioServer.GetBusName).ToArray();
        Check(AudioBuses.All.All(n => names.Contains(n)), "all six buses exist: " + string.Join(",", names));
        Check(names.Count(n => n == AudioBuses.Music) == 1 && names.Count(n => n == AudioBuses.Voice) == 1, "EnsureLayout twice makes no duplicates");
        Check(AudioServer.GetBusName(0) == AudioBuses.Master, "Master is bus 0");
        foreach (var name in AudioBuses.All.Skip(1))
            Check(AudioServer.GetBusSend(AudioServer.GetBusIndex(name)) == AudioBuses.Master, $"{name} sends to Master");
        var master = AudioServer.GetBusIndex(AudioBuses.Master);
        var limiters = Enumerable.Range(0, AudioServer.GetBusEffectCount(master)).Count(i => AudioBuses.IsLimiter(AudioServer.GetBusEffect(master, i)));
        Check(limiters == 1, "Master carries exactly one limiter");
        Check(AudioServer.GetBusEffectCount(AudioServer.GetBusIndex(AudioBuses.Music)) == 0, "the Music bus is empty");
        var layout = ResourceLoader.Load<AudioBusLayout>("res://assets/audio/default_bus_layout.tres");
        Check(layout != null, "the shipped default_bus_layout.tres loads as an AudioBusLayout");
    }

    private void TestSettings()
    {
        AudioSettings.Reset();
        Check(Math.Abs(AudioSettings.Get(AudioBuses.Master) - 0.8f) < 1e-5f, "Master defaults to 0.8");
        AudioSettings.Set(AudioBuses.Ambience, 0.5f);
        Check(Math.Abs(AudioSettings.Get(AudioBuses.Ambience) - 0.5f) < 1e-5f, "a set volume reads back");
        Check(Math.Abs(AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(AudioBuses.Ambience)) - AudioSettings.ToDb(0.5f)) < 1e-3f, "the bus follows the setting");
        AudioSettings.Set(AudioBuses.Effects, 5f);
        Check(AudioSettings.Get(AudioBuses.Effects) == 1f, "a volume above 1 is clamped to 1");
        AudioSettings.Set(AudioBuses.Effects, -1f);
        Check(AudioSettings.Get(AudioBuses.Effects) == 0f && AudioSettings.ToDb(0) <= -80f, "a volume below 0 is clamped to silence");
        AudioSettings.Set(AudioBuses.UI, float.NaN);
        Check(Math.Abs(AudioSettings.Get(AudioBuses.UI) - 0.6f) < 1e-5f, "NaN is ignored");
        AudioSettings.Set("Nonsense", 0.3f);
        Check(AudioSettings.Get("Nonsense") == 1f, "an unknown bus is ignored");
        AudioSettings.Mute(AudioBuses.Voice, true);
        Check(AudioServer.IsBusMute(AudioServer.GetBusIndex(AudioBuses.Voice)) && AudioSettings.IsMuted(AudioBuses.Voice), "mute reaches the bus");
        var path = "user://tests/audio/audio.cfg";
        DirAccess.MakeDirRecursiveAbsolute("user://tests/audio");
        AudioSettings.Set(AudioBuses.Master, 0.3f);
        Check(AudioSettings.Save(path) == Error.Ok, "settings save");
        AudioSettings.Reset();
        Check(!AudioSettings.IsMuted(AudioBuses.Voice) && Math.Abs(AudioSettings.Get(AudioBuses.Master) - 0.8f) < 1e-5f, "reset restores the defaults and unmutes");
        AudioSettings.Load(path);
        Check(Math.Abs(AudioSettings.Get(AudioBuses.Master) - 0.3f) < 1e-5f && AudioSettings.IsMuted(AudioBuses.Voice), "settings load back, mute included");
        AudioSettings.Load("user://tests/audio/missing.cfg");
        Check(Math.Abs(AudioSettings.Get(AudioBuses.Master) - 0.3f) < 1e-5f, "a missing file leaves the values alone");
        DirAccess.RemoveAbsolute(path);
        AudioSettings.Reset();
    }

    // ---- the sounds ----

    private void TestSounds()
    {
        var ids = SoundBank.All.Select(r => r.Id).ToArray();
        Check(ids.Distinct().Count() == ids.Length, "every sound id is unique");
        foreach (var ground in Sounds.Grounds)
            Check(ids.Contains(Sounds.Step(ground, 0)) && ids.Contains(Sounds.Step(ground, 1)), $"two footsteps on {ground}");
        foreach (var id in new[] { Sounds.WindSoft, Sounds.WindGust, Sounds.Sea, Sounds.Rain, Sounds.Insects, Sounds.Fire, Sounds.Wade, Sounds.Swim, Sounds.Splash,
                     Sounds.Pickup, Sounds.Putdown, Sounds.Eat, Sounds.UiTick, Sounds.UiHover, Sounds.UiConfirm }.Concat(Sounds.Birds))
            Check(ids.Contains(id), "the bank has " + id);
        Check(SoundBank.All.All(r => AudioBuses.All.Contains(r.Bus) && r.Bus != AudioBuses.Master && r.Bus != AudioBuses.Music), "every sound plays on Ambience, Effects or UI");
        var samples = 0;
        foreach (var recipe in SoundBank.All)
        {
            var a = SoundBank.Render(recipe);
            var b = SoundBank.Render(recipe);
            samples += a.Length;
            Check(a.Length == b.Length && a.AsSpan().SequenceEqual(b), recipe.Id + " is deterministic");
            Check(a.All(float.IsFinite), recipe.Id + " has no NaN or infinity");
            var peak = a.Max(MathF.Abs);
            Check(peak <= recipe.Peak + 1e-4f && peak < 0.5f, $"{recipe.Id} peaks at {peak:0.00}, below its limit {recipe.Peak:0.00} and well under full scale");
            Check(peak > recipe.Peak - 0.01f, recipe.Id + " is not silent");
            var rms = MathF.Sqrt(a.Sum(v => v * v) / a.Length);
            Check(rms > 0.004f, $"{recipe.Id} has body (rms {rms:0.000})");
            var pcm = Dsp.ToPcm16(a);
            Check(pcm.Length == a.Length * 2, recipe.Id + " is 16-bit mono PCM");
            if (recipe.Loop)
            {
                Check(a.Length >= Dsp.Rate * 5 && a.Length <= Dsp.Rate * 14, recipe.Id + " loop length is 5 to 14 seconds");
                // The seam: the jump from the last sample to the first is no bigger than the sound's own sample-to-sample motion.
                double sum = 0; var worst = 0f;
                for (var i = 1; i < a.Length; i++) { var d = a[i] - a[i - 1]; sum += d * d; worst = MathF.Max(worst, MathF.Abs(d)); }
                var rmsStep = MathF.Sqrt((float)(sum / (a.Length - 1)));
                var seam = MathF.Abs(a[0] - a[^1]);
                Check(seam <= 3f * rmsStep + 0.002f && seam <= worst + 1e-6f, $"{recipe.Id} loops without a click (seam {seam:0.0000}, typical step {rmsStep:0.0000})");
                var mean = a.Sum() / a.Length;
                Check(MathF.Abs(mean) < 0.01f, recipe.Id + " has no offset");
            }
            else
            {
                Check(a.Length >= Dsp.Rate * 0.02 && a.Length <= Dsp.Rate * 1.5, recipe.Id + " is a short one-shot");
                Check(MathF.Abs(a[0]) < 0.01f && MathF.Abs(a[^1]) < 0.01f, recipe.Id + " starts and ends at silence");
            }
        }
        Check(samples < Dsp.Rate * 400, $"the whole bank is {samples} samples, light enough to make on a worker at start");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        foreach (var recipe in SoundBank.All) SoundBank.Render(recipe);
        GD.Print($"AUDIO_SYNTH: the whole bank in {elapsed.ElapsedMilliseconds} ms on one thread ({samples} samples, {samples / 1024 * 2 / 1024.0:0.0} MB as 16-bit)");
        Check(Dsp.ToWav(new float[10]).Length == 44 + 20, "a WAV is a 44-byte header and the samples");
    }

    // ---- the director on a world filled in by hand ----

    private static void Steps(AudioDirector d, FakeSource f, double seconds, double dt = 0.05)
    {
        for (var t = 0.0; t < seconds; t += dt) d.Update(dt);
    }

    private static CommandHandled Handled(string op, bool ok = true, bool preview = false, bool replayed = false, string principal = CommandHost.PlayerPrincipal) =>
        new("{}", principal, op, ok, "a1", ok ? null : "refused", null, 1, replayed, preview, 1.0);

    private async System.Threading.Tasks.Task TestDirector()
    {
        var source = new FakeSource { S = new AudioSnapshot { Daylight = 1f, Outdoors = true } };
        var director = AudioDirector.Create(this, source, null, loadSettings: false, seed: 7);
        director.LoadAllNow();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(director.StreamsReady, "the director is ready once its sounds are wrapped");

        // Silence: standing in clear daylight for two seconds starts no one-shot.
        Steps(director, source, 2);
        Check(director.Log.Count == 0, "standing still on a calm day plays no effect");
        Check(director.LoopTarget(Sounds.Rain) == 0 && director.LoopTarget(Sounds.Sea) == 0 && director.LoopTarget(Sounds.Insects) == 0, "no rain, sea or night insects in calm daylight");
        Check(director.LoopTarget(Sounds.WindSoft) > 0.5f && director.LoopTarget(Sounds.WindGust) < 0.05f, "a calm day has the soft wind only");

        // Hands: only a successful, real, player command makes its sound.
        void Expect(CommandHandled h, string sound, string why)
        {
            var before = director.PlayedCount(sound); var all = director.Log.Count;
            director.OnHandled(h);
            var made = director.PlayedCount(sound) - before;
            Check(sound == "" ? director.Log.Count == all : made == 1 && director.Log.Count == all + 1, why);
        }
        Expect(Handled("item.pickup"), Sounds.Pickup, "item.pickup plays the pickup");
        Expect(Handled("item.gather"), Sounds.Pickup, "item.gather plays the pickup");
        Expect(Handled("inventory.take"), Sounds.Pickup, "inventory.take plays the pickup");
        Expect(Handled("inventory.drop"), Sounds.Putdown, "inventory.drop plays the putdown");
        Expect(Handled("inventory.store"), Sounds.Putdown, "inventory.store plays the putdown");
        Expect(Handled("item.pickup", ok: false), "", "a refused pickup is silent");
        Expect(Handled("item.pickup", preview: true), "", "a previewed pickup is silent");
        Expect(Handled("item.pickup", replayed: true), "", "a replayed pickup is silent");
        Expect(Handled("inventory.drop", principal: CommandHost.CompanionPrincipal), "", "the companion's drop is silent");
        Expect(Handled("entity.place"), "", "an unrelated op is silent");
        Expect(Handled("inventory.use"), "", "inventory.use alone is silent (the eating sound comes from FoodSound)");
        var eaten = director.PlayedCount(Sounds.Eat);
        director.OnFood("apple");
        Check(director.PlayedCount(Sounds.Eat) == eaten + 1, "FoodSound plays the eating sound");
        Check(director.Log.All(p => p.Bus == AudioBuses.Effects), "hands and mouth play on the Effects bus");

        // Feet.
        var ground = "sand";
        source.S.GroundNow = () => ground;
        source.S.PlayerSpeedMps = 0.32f;
        var logged = director.Log.Count;
        Steps(director, source, 1);
        var steps = director.Log.Skip(logged).Where(p => p.Id.StartsWith("step_", StringComparison.Ordinal)).ToArray();
        Check(steps.Length is >= 2 and <= 4 && steps.All(p => p.Id.StartsWith("step_sand_", StringComparison.Ordinal)), $"walking a second on sand makes {steps.Length} sand steps");
        Check(steps.Select(p => p.Id).Distinct().Count() == 2, "steps alternate between two variants");
        foreach (var g in new[] { "grass", "dirt", "stone", "wood" })
        {
            ground = g;
            logged = director.Log.Count;
            Steps(director, source, 0.6);
            Check(director.Log.Skip(logged).Any(p => p.Id.StartsWith($"step_{g}_", StringComparison.Ordinal)), $"walking on {g} sounds like {g}");
        }
        ground = "lava";
        logged = director.Log.Count;
        Steps(director, source, 0.6);
        Check(director.Log.Skip(logged).Any(p => p.Id.StartsWith("step_grass_", StringComparison.Ordinal)), "an unknown ground falls back to grass");
        source.S.PlayerSpeedMps = 0;
        logged = director.Log.Count;
        Steps(director, source, 1);
        Check(director.Log.Count == logged, "standing still makes no steps");
        source.S.PlayerSpeedMps = 0.32f; source.S.OnFloor = false;
        Steps(director, source, 0.1);
        logged = director.Log.Count;
        Steps(director, source, 0.5);
        Check(director.Log.Count == logged, "no steps in the air");
        source.S.OnFloor = true;
        Steps(director, source, 0.05);
        Check(director.Log.Skip(logged).Any(p => p.Id.StartsWith("step_", StringComparison.Ordinal)), "landing after a jump is a firm step");

        // Water.
        source.S.PlayerSpeedMps = 0;
        Steps(director, source, 0.3);
        var splash = director.PlayedCount(Sounds.Splash);
        source.S.Water = FeetWater.Wading;
        Steps(director, source, 0.1);
        Check(director.PlayedCount(Sounds.Splash) == splash + 1, "stepping into water splashes once");
        source.S.PlayerSpeedMps = 0.2f;
        var wades = director.PlayedCount(Sounds.Wade);
        var drySteps = director.PlayedPrefix("step_");
        Steps(director, source, 1);
        Check(director.PlayedCount(Sounds.Wade) >= wades + 2 && director.PlayedPrefix("step_") == drySteps, "wading makes wading steps, not footsteps");
        source.S.Water = FeetWater.Swimming;
        var swims = director.PlayedCount(Sounds.Swim);
        Steps(director, source, 2.5);
        Check(director.PlayedCount(Sounds.Swim) is >= 1 and <= 3 && director.PlayedCount(Sounds.Splash) == splash + 1, "swimming strokes now and then, with no second splash");
        source.S.PlayerSpeedMps = 0;
        swims = director.PlayedCount(Sounds.Swim);
        Steps(director, source, 3);
        Check(director.PlayedCount(Sounds.Swim) == swims, "treading water is quiet");
        source.S.Water = FeetWater.Dry;
        Steps(director, source, 0.2);

        // Ambience follows the world.
        source.S.Rain01 = 1f;
        Steps(director, source, 12);
        Check(director.LoopTarget(Sounds.Rain) > 0.9f && director.LoopLevel(Sounds.Rain) > 0.8f && director.LoopPlaying(Sounds.Rain), "in rain the rain loop eases up and plays");
        source.S.Rain01 = 0;
        Steps(director, source, 15);
        Check(director.LoopTarget(Sounds.Rain) == 0 && !director.LoopPlaying(Sounds.Rain), "when the rain stops its loop fades and stops");
        source.S.WindMps = 0.35f;
        Steps(director, source, 1);
        Check(director.LoopTarget(Sounds.WindGust) > 0.8f && director.LoopTarget(Sounds.WindSoft) < 0.5f, "strong wind brings the gusty loop forward");
        source.S.WindMps = 0.12f;
        source.S.SeaNear01 = 1f;
        Steps(director, source, 8);
        Check(director.LoopTarget(Sounds.Sea) > 0.8f && director.LoopPlaying(Sounds.Sea), "at the shore the sea is heard");
        source.S.SeaNear01 = 0;
        source.S.Daylight = 0f;
        Steps(director, source, 8);
        Check(director.LoopTarget(Sounds.Insects) > 0.7f && director.LoopPlaying(Sounds.Insects), "at night the insects sing");
        source.S.Season = "winter";
        Steps(director, source, 1);
        Check(director.LoopTarget(Sounds.Insects) < 0.25f, "in winter they are few");
        source.S.Season = "summer"; source.S.Daylight = 1f;
        Steps(director, source, 12);
        Check(director.LoopTarget(Sounds.Insects) == 0 && !director.LoopPlaying(Sounds.Insects), "by day the insects are gone");
        source.S.Outdoors = false; source.S.Rain01 = 1f; source.S.SeaNear01 = 1f;
        Steps(director, source, 1);
        Check(director.LoopTarget(Sounds.Rain) < 0.5f && director.LoopTarget(Sounds.Sea) < 0.4f && director.LoopTarget(Sounds.WindSoft) < 0.2f, "indoors the weather and the sea are muffled");
        source.S.Outdoors = true; source.S.Rain01 = 0; source.S.SeaNear01 = 0;
        Steps(director, source, 12);
        source.S.Paused = true;
        Steps(director, source, 1);
        Check(new[] { Sounds.WindSoft, Sounds.WindGust, Sounds.Sea, Sounds.Rain, Sounds.Insects }.All(id => director.LoopTarget(id) == 0), "paused, every ambient loop fades toward silence");
        var pausedLog = director.Log.Count;
        source.S.PlayerSpeedMps = 0.32f;
        Steps(director, source, 1);
        Check(director.Log.Count == pausedLog, "paused, there are no footsteps");
        source.S.Paused = false;
        source.S.PlayerSpeedMps = 0;

        // Fire.
        source.S.PlayerPosition = Vector3.Zero;
        source.S.Fires = new[] { new LitFire("camp:far", new Vector3(30, 0, 0)), new LitFire("camp:near", new Vector3(2, 0, 0)) };
        Steps(director, source, 6);
        Check(director.FirePlaying && director.FireAt == "camp:near", "a lit campfire within earshot plays, at the nearest");
        source.S.Fires = new[] { new LitFire("camp:far", new Vector3(30, 0, 0)) };
        Steps(director, source, 15);
        Check(!director.FirePlaying && director.FireAt == null, "a fire out of earshot, or put out, is silent");
        source.S.Fires = Array.Empty<LitFire>();

        // Birds: by clear day, outdoors only.
        int Birds() => Sounds.Birds.Sum(director.PlayedCount);
        var birds = Birds();
        Steps(director, source, 90);
        Check(Birds() - birds >= 4 && Birds() - birds <= 20, $"{Birds() - birds} bird calls in a minute and a half of clear day");
        birds = Birds();
        source.S.Daylight = 0f;
        Steps(director, source, 90);
        Check(Birds() == birds, "no birds at night");
        source.S.Daylight = 1f; source.S.Rain01 = 0.8f;
        Steps(director, source, 90);
        Check(Birds() == birds, "no birds in rain");
        source.S.Rain01 = 0; source.S.Outdoors = false;
        Steps(director, source, 90);
        Check(Birds() == birds, "no birds indoors");
        Check(director.Log.Where(p => Sounds.Birds.Contains(p.Id)).All(p => p.Bus == AudioBuses.Ambience), "birds play on the Ambience bus");
        source.S.Outdoors = true;

        // UI: a button that appears in the tree ticks when pressed; a sweep is rate-limited.
        var button = new Button { Text = "Test" };
        AddChild(button);
        Steps(director, source, 0.2);
        var ticks = director.PlayedCount(Sounds.UiTick);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        Check(director.PlayedCount(Sounds.UiTick) == ticks + 1, "a button added to the tree ticks when pressed");
        button.EmitSignal(BaseButton.SignalName.Pressed);
        Check(director.PlayedCount(Sounds.UiTick) == ticks + 1, "two presses within 50 ms make one tick");
        Steps(director, source, 0.2);
        button.EmitSignal(BaseButton.SignalName.MouseEntered);
        Check(director.PlayedCount(Sounds.UiHover) == 1, "hovering a button makes the quiet hover tick");
        Check(director.Log.Where(p => p.Id.StartsWith("ui_", StringComparison.Ordinal)).All(p => p.Bus == AudioBuses.UI), "ui sounds play on the UI bus");
        button.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // The same world and seed give the same sounds.
        var script = director.Log.Select(p => p.Id).ToArray();
        director.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var again = RunBriefSession(7);
        var once = RunBriefSession(7);
        Check(again.SequenceEqual(once) && again.Length > 0, "the same seed and the same world make the same sounds in the same order");
        Check(script.Length > 0, "the first session logged sounds");
    }

    private string[] RunBriefSession(uint seed)
    {
        var source = new FakeSource { S = new AudioSnapshot { Daylight = 1f, Outdoors = true, PlayerSpeedMps = 0.32f } };
        var d = AudioDirector.Create(this, source, null, false, seed);
        d.LoadAllNow();
        Steps(d, source, 40);
        var log = d.Log.Select(p => p.Id).ToArray();
        d.Free();
        return log;
    }

    // ---- the director on a real command host ----

    private async System.Threading.Tasks.Task TestRealHost()
    {
        CommandHost.DefaultTeamSightIntervalS = 0;
        CommandHost.DefaultSharedSight = false;
        var room = RoomData.Load(RoomWorld.DefaultRoom);
        var holder = new Node3D { Name = "HostWorld" };
        AddChild(holder);
        holder.AddChild(RoomBuilder.Build(room));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false, Position = room.SpawnFor("player").PositionM + Vector3.Up * 0.008f };
        holder.AddChild(player);
        var companion = new CompanionAvatar { Name = "Companion", Position = room.SpawnFor("companion", room.SpawnFor("player")).PositionM + Vector3.Up * 0.008f };
        holder.AddChild(companion);
        companion.BindPlayer(player);
        var host = CommandHost.Create(holder, room, player, companion, null, "user://tests/audio/host/" + room.ManifestSha256[..16] + "/inventions.json");
        var source = new FakeSource { S = new AudioSnapshot { Daylight = 1f } };
        var director = AudioDirector.Create(holder, source, host);
        director.LoadAllNow();
        var seen = director.HandledSeen;
        var query = new JsonObject { ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "audio-q1", ["room_id"] = "test_room", ["op"] = "room.describe", ["args"] = new JsonObject() };
        host.Handle(query.ToJsonString(), CommandHost.PlayerPrincipal);
        Check(director.HandledSeen > seen, "the director is subscribed to the host's Handled event: it saw the host answer a message");
        Check(director.Log.Count == 0, "a query makes no sound");
        director.QueueFree();
        holder.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
