using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EnFractal.Native;
using EnFractal.Native.Kernel;

namespace EnFractal.Tests.Kernel;

/// <summary>
/// The meters packet's captures (docs/runs/RUN-2-MECHANICS.md, M2): the HUD as the player sees it, in the real window, a
/// before set and an after set from the same script. It uses only what the HUD and the host have always offered, so the same
/// scenarios run on the code before the meters and on the code after. Run it with a scratch profile (APPDATA elsewhere), never
/// while someone plays: the window must not be open beside a "Garage Game*" (or older "EnFractal*") window.
///
/// User arguments (after "--"): --out=DIR [--tag=before|after] [--scenarios=play_challenge,play_creative,wheel,journal,marker,spend,pulse,probe]
/// [--room=ID|DIR] [--hour=15] [--date=2026-10-06]. The probe scenario prints the team's discovered map after a walk (it needs
/// no window and also runs headless).
/// </summary>
public partial class PlayHudTestCapture : Node
{
    private readonly Dictionary<string, string> _args = new();
    private RoomWorld _world = null!;
    private RoomHud _hud = null!;
    private string _out = "";
    private string _tag = "";

    public override async void _Ready()
    {
        try
        {
            foreach (var argument in OS.GetCmdlineUserArgs())
            {
                var text = argument.TrimStart('-');
                var split = text.IndexOf('=');
                _args[split < 0 ? text : text[..split]] = split < 0 ? "true" : text[(split + 1)..];
            }
            var scenarios = Arg("scenarios", "play_challenge,play_creative,wheel,journal").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var windowed = DisplayServer.GetName() != "headless";
            if (!windowed && scenarios.Any(s => s != "probe" && s != "alloc" && s != "dump"))
            {
                GD.Print("PLAY_HUD_CAPTURE: needs a real window on the GPU; --headless renders nothing.");
                GetTree().Quit(2);
                return;
            }
            _out = Arg("out", "");
            _tag = Arg("tag", "capture");
            if (_out.Length > 0) System.IO.Directory.CreateDirectory(_out);
            Engine.MaxFps = 60;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            // A fresh scratch profile would play the opening intro first, and the intro takes every input: --no-intro skips it.
            if (Arg("no-intro", "") == "true")
            {
                const string profile = "user://capture_profile/avatar_profile_v1.cfg";
                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://capture_profile"));
                var config = new ConfigFile();
                config.SetValue("profile", "version", 1);
                config.SetValue("profile", RoomHud.PlayIntroProfileKey, false);
                config.Save(profile);
                RoomHud.ProfilePath = profile;
            }
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            if (Arg("room", "").Length > 0) _world.RoomDirectory = RoomWorld.ResolveRoom(Arg("room", "").Replace('\\', '/'));
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 90 && !_world.WorldReady && _world.LoadError.Length == 0;) await NextFrame();
            if (!_world.WorldReady) throw new InvalidOperationException("room did not load: " + _world.LoadError);
            _hud = _world.GetNode<RoomHud>("RoomHud");
            _world.Player.ReadKeyboard = false;
            _world.Look.SetClock(float.Parse(Arg("hour", "15"), CultureInfo.InvariantCulture),
                DateTime.ParseExact(Arg("date", "2026-10-06"), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfYear);
            _world.Look.SetLamps(null);
            for (var i = 0; i < 150; i++) await NextFrame();
            foreach (var scenario in scenarios)
            {
                GD.Print($"PLAY_HUD_CAPTURE scenario={scenario} tag={_tag}");
                switch (scenario)
                {
                    case "play_challenge": await PlayChallenge(); break;
                    case "play_creative": await PlayCreative(); break;
                    case "wheel": await Wheel(); break;
                    case "journal": await Journal(); break;
                    case "low": await Low(); break;
                    case "numbers": await Numbers(); break;
                    case "big": await Big(); break;
                    case "wheel_empty": await WheelEmpty(); break;
                    case "journal_pages": await JournalPages(); break;
                    case "marker": await Marker(); break;
                    case "spend": await Spend(); break;
                    case "pulse": await Pulse(); break;
                    // The Fire behaviours packet (PlayHudTestCapture.FireBehaviours.cs).
                    case "fractal": await FractalCapture(); break;
                    case "fire": await FireCapture(); break;
                    case "probe": await Probe(); break;
                    case "alloc": await Alloc(); break;
                    case "dump": await Dump(); break;
                    default: throw new ArgumentException("unknown scenario " + scenario);
                }
            }
            GD.Print($"PLAY_HUD_CAPTURE_DONE tag={_tag}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("Play HUD capture failed: " + error);
            GetTree().Quit(1);
        }
    }

    private string Arg(string name, string fallback) => _args.TryGetValue(name, out var value) ? value : fallback;
    private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task Frames(int count) { for (var i = 0; i < count; i++) await NextFrame(); }
    private CommandHost Host => CommandHost.Of(_world)!;

    private async Task Save(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgb8);
        var path = System.IO.Path.Combine(_out, $"{_tag}_{name}.png");
        image.SavePng(path);
        GD.Print($"PLAY_HUD_CAPTURE_FRAME {path} {image.GetWidth()}x{image.GetHeight()}");
    }

    /// <summary>The player standing on the room's floor with the Gubble in front of it, the third-person view, nothing running.</summary>
    private async Task Stage()
    {
        var player = _world.Player;
        var companion = _world.Companion;
        _hud.SetViewMode(1);
        player.SetControlInput(Vector2.Zero);
        companion.Stay();
        var spawn = _world.Room.SpawnFor("player").PositionM;
        var feet = Arg("room", "").Length > 0 ? spawn + Vector3.Up * 0.005f : new Vector3(0.0f, 0.003f, 0.9f);
        player.TryTeleportTo(feet);
        player.Rotation = Vector3.Zero;
        player.ResetPhysicsInterpolation();
        var ahead = player.GlobalBasis * new Vector3(0.1f, 0, -0.3f);
        companion.TryTeleportTo(feet + ahead);
        companion.ResetPhysicsInterpolation();
        await Frames(60);
    }

    private async Task Cast(int times)
    {
        var host = Host;
        for (var i = 0; i < times; i++)
        {
            host.PlayerEffect("glow", _world.Companion.GlobalPosition + new Vector3(0.1f * i, 0, 0.1f));
            await Frames(2);
        }
    }

    /// <summary>Challenge, hurt by six quarters of a heart (30 health points), two Glows cast: the meters mid-play.</summary>
    private async Task PlayChallenge()
    {
        var host = Host;
        host.PlayerMode("challenge");
        await Stage();
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        await Cast(2);
        await Frames(90);
        await Save("play_challenge");
    }

    private async Task PlayCreative()
    {
        var host = Host;
        host.PlayerMode("creative");
        await Stage();
        await Cast(1);
        await Frames(90);
        await Save("play_creative");
    }

    /// <summary>The Gubble's wheel held open: the abilities with whatever the HUD says about them.</summary>
    private async Task Wheel()
    {
        var host = Host;
        host.PlayerMode("challenge");
        await Stage();
        _hud.LightForTests = _ => 0.9f;
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        await Frames(40);
        await Save("wheel");
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        await Frames(10);
        _hud.LightForTests = null;
    }

    /// <summary>An in-memory vitals store for the scenarios that need a body the game would only reach by playing a long time.</summary>
    private sealed class CaptureStore(PlayerModesProfile profile) : IPlayerVitalsStore
    {
        private PlayerModesProfile _state = profile;
        public PlayerModesProfile Load() => _state;
        public void Save(PlayerModesProfile next) => _state = next;
    }

    /// <summary>One heart left (four quarters): the hearts shake, the pool half spent.</summary>
    private async Task Low()
    {
        var host = Host;
        host.PlayerMode("challenge");
        await Stage();
        host.ApplyPlayerDamage(80f, PlayerDamageSource.Fall);
        await Cast(1);
        await Frames(50);
        await Save("low");
    }

    /// <summary>The numbers option on: hearts, bar and pips say their numbers.</summary>
    private async Task Numbers()
    {
        var host = Host;
        host.PlayerMode("challenge");
        await Stage();
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        _hud.SetMeterNumbers(true);
        await Cast(1);
        await Frames(90);
        await Save("numbers");
        _hud.SetMeterNumbers(false);
    }

    /// <summary>A body far along: thirty hearts (half hurt) in two rows, ten bands of bar in two rows.</summary>
    private async Task Big()
    {
        var host = Host;
        var fractals = new PlayerFractals(170, 0, 25, 9);
        var body = PlayerVitals.Of(50f, 100f, 0.4f, 0.4f, Array.Empty<string>(), 0, fractals, PlayerVitals.UnsetPool);
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(body)));
        await Stage();
        await Frames(60);
        await Save("big");
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh));
        host.PlayerMode("creative");
    }

    /// <summary>The wheel with the pool spent: what the abilities say they need.</summary>
    private async Task WheelEmpty()
    {
        var host = Host;
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh));
        host.PlayerMode("challenge");
        await Stage();
        for (var i = 0; i < 3; i++)
        {
            host.PlayerEffect("glow", _world.Companion.GlobalPosition + new Vector3(0.1f * i, 0, 0.1f));
            host.PlayerGoal("stop");
            await Frames(2);
        }
        _hud.LightForTests = _ => 0.9f;
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        await Frames(40);
        await Save("wheel_empty");
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        await Frames(10);
        _hud.LightForTests = null;
    }

    /// <summary>The journal's Expertise page and its Abilities page (with the prices).</summary>
    private async Task JournalPages()
    {
        Host.PlayerMode("challenge");
        await Stage();
        _hud.OpenJournal();
        await Frames(80);
        _hud.Journal.OpenPage(Native.Ui.JournalPages.Discoveries);
        await Frames(40);
        await Save("journal_expertise");
        _hud.Journal.OpenPage(Native.Ui.JournalPages.Abilities);
        await Frames(40);
        await Save("journal_abilities");
        _hud.CloseJournal();
        await Frames(70);
    }

    /// <summary>
    /// The pips as a row under the bar, always (decision 51): first person, turned away from the Gubble and facing it (the row is the
    /// same in both); in Challenge with a Glow spent, with the numbers on, and in Creative's shadow pool.
    /// </summary>
    private async Task Marker()
    {
        var host = Host;
        var player = _world.Player;
        var companion = _world.Companion;
        host.PlayerMode("challenge");
        await Stage();
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        await Cast(1);
        void Face(Vector3 facing) => player.Rotation = new Vector3(0, Mathf.Atan2(-facing.X, -facing.Z), 0);
        _hud.SetViewMode(0);
        Face(companion.GlobalPosition - player.GlobalPosition);
        await Frames(60);
        await Save("marker_in_frame");
        Face(player.GlobalPosition - companion.GlobalPosition);
        await Frames(60);
        await Save("marker_challenge");
        _hud.SetMeterNumbers(true);
        await Frames(30);
        await Save("marker_numbers");
        _hud.SetMeterNumbers(false);
        host.PlayerMode("creative");
        await Frames(60);
        await Save("marker_creative");
        Face(companion.GlobalPosition - player.GlobalPosition);
        _hud.SetViewMode(1);
        await Frames(30);
    }

    /// <summary>The journal's Character page with Fractals in hand: at the camp (the two buttons), after a heart, and away from the camp.</summary>
    private async Task Spend()
    {
        var host = Host;
        var player = _world.Player;
        var body = PlayerVitals.Of(100f, 100f, 0.4f, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(7, 7, 0, 0), PlayerVitals.UnsetPool);
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(body)));
        host.PlayerMode("challenge");
        await Stage();
        player.TryTeleportTo(host.Room.SpawnFor("player").PositionM);
        player.ResetPhysicsInterpolation();
        await Frames(30);
        _hud.OpenJournal();
        await Frames(80);
        _hud.Journal.OpenPage(Native.Ui.JournalPages.Character);
        await Frames(40);
        await Save("spend_camp");
        _hud.Journal.Press(Native.Ui.FractalSpend.HeartTarget);
        await Frames(40);
        await Save("spend_heart");
        _hud.CloseJournal();
        await Frames(70);
        await Save("spend_hearts_drawn");
        foreach (var offset in new[] { new Vector3(0.8f, 0, 0), new Vector3(-0.8f, 0, 0), new Vector3(0, 0, 0.8f), new Vector3(0, 0, -0.8f) })
            if (player.TryTeleportTo(host.Room.SpawnFor("player").PositionM + offset) && !host.PlayerAtCamp()) break;
        player.ResetPhysicsInterpolation();
        _hud.OpenJournal();
        await Frames(80);
        _hud.Journal.OpenPage(Native.Ui.JournalPages.Character);
        await Frames(40);
        await Save("spend_away");
        _hud.CloseJournal();
        await Frames(70);
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh));
        host.PlayerMode("creative");
    }

    /// <summary>
    /// The journal's book pulsing in its dock (decision 53): six Fractals in hand, the Gubble in frame with the pips in their row under the
    /// bar; one frame at the bottom of a breath and one at the top, then the book opened (the pulse over) and closed again (still quiet).
    /// </summary>
    private async Task Pulse()
    {
        var host = Host;
        var body = PlayerVitals.Of(100f, 100f, 0.4f, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(6, 6, 0, 0), PlayerVitals.UnsetPool);
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(body)));
        host.PlayerMode("challenge");
        await Stage();
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        await Cast(1);
        await Frames(60);
        var dock = _hud.Book.Dock;
        GD.Print($"PLAY_HUD_CAPTURE pulse: pulsing {_hud.JournalPulsing}, row shown {_hud.PipsRow.Shown}");
        for (var guard = 0; guard < 400 && dock.PulseLevel > 0.03f; guard++) await NextFrame();
        await Save("pulse_low");
        for (var guard = 0; guard < 400 && dock.PulseLevel < 0.97f; guard++) await NextFrame();
        await Save("pulse_high");
        _hud.OpenJournal();
        await Frames(80);
        _hud.CloseJournal();
        await Frames(90);
        GD.Print($"PLAY_HUD_CAPTURE pulse after the book was opened: pulsing {_hud.JournalPulsing}");
        await Save("pulse_after_open");
        host.ConfigurePlayerVitals(new CaptureStore(PlayerModesProfile.Fresh));
        host.PlayerMode("creative");
    }

    /// <summary>The journal's index and its character page, open at the spread.</summary>
    private async Task Journal()
    {
        Host.PlayerMode("challenge");
        await Stage();
        _hud.OpenJournal();
        await Frames(80);
        await Save("journal_index");
        _hud.Journal.OpenPage(Native.Ui.JournalPages.Character);
        await Frames(40);
        await Save("journal_character");
        _hud.CloseJournal();
        await Frames(70);
    }

    /// <summary>
    /// The meters' own pictures on a sheet, drawn in software (no window needed): each heart, the pips, and the minimap's picture
    /// after a walk, scaled up and laid on the HUD's panel colour. To look at the shapes and colours without a GPU.
    /// </summary>
    private async Task Dump()
    {
        var host = Host;
        await Stage();
        var panel = new Color(0.075f, 0.13f, 0.13f, 0.94f);
        const int scale = 5;
        var sheet = Image.CreateEmpty(900, 420, false, Image.Format.Rgba8);
        sheet.Fill(panel);
        void Blit(byte[] pixels, int side, int x0, int y0, Color? tint = null)
        {
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                {
                    var at = (y * side + x) * 4;
                    var colour = new Color(pixels[at] / 255f, pixels[at + 1] / 255f, pixels[at + 2] / 255f, pixels[at + 3] / 255f);
                    if (tint is { } t) colour = new Color(colour.R * t.R, colour.G * t.G, colour.B * t.B, colour.A * t.A);
                    for (var sy = 0; sy < scale; sy++)
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var (px, py) = (x0 + x * scale + sx, y0 + y * scale + sy);
                            var under = sheet.GetPixel(px, py);
                            sheet.SetPixel(px, py, under.Lerp(new Color(colour, 1f), colour.A));
                        }
                }
        }
        for (var k = 0; k <= 4; k++) Blit(Native.Ui.MeterTextures.HeartPixels(k), Native.Ui.MeterTextures.HeartPx, 10 + k * 150, 10);
        Blit(Native.Ui.MeterTextures.HeartPixels(4, grey: true), Native.Ui.MeterTextures.HeartPx, 760, 10);
        // The pips as the Gubble's aura tints them: a pip you have, a spent one.
        var aura = new Color("a18cc3").Lightened(0.15f);
        Blit(PipPixels(0), Native.Ui.MeterTextures.PipPx, 10, 190, aura);
        Blit(PipPixels(1), Native.Ui.MeterTextures.PipPx, 190, 190, Colors.White);
        // The shadow pool in the row under the bar (PipsMarker): a pip you have, then a spent one, in the grey of Creative's hearts and bar.
        Blit(PipPixels(0), Native.Ui.MeterTextures.PipPx, 370, 190, Native.Ui.PipsMarker.GreyHave);
        Blit(PipPixels(1), Native.Ui.MeterTextures.PipPx, 550, 190, Native.Ui.PipsMarker.GreySpent);
        if (_out.Length > 0) sheet.SavePng(System.IO.Path.Combine(_out, $"{_tag}_meter_pictures.png"));
        GD.Print("PLAY_HUD_CAPTURE_FRAME " + System.IO.Path.Combine(_out, $"{_tag}_meter_pictures.png"));
    }

    private static byte[] PipPixels(int kind)
    {
        var texture = kind switch { 0 => Native.Ui.MeterTextures.PipFull, _ => Native.Ui.MeterTextures.PipSpent };
        using var image = texture.GetImage();
        image.Convert(Image.Format.Rgba8);
        return image.GetData();
    }

    /// <summary>Managed bytes a piece of the meters allocates per call, in the steady state (a scratch probe for the frame cost).</summary>
    private async Task Alloc()
    {
        var host = Host;
        await Stage();
        host.PlayerMode("challenge");
        await Frames(120);
        long Bytes(Action action, int times = 2000)
        {
            for (var i = 0; i < 50; i++) action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < times; i++) action();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / times;
        }
        var camera = _world.GetViewport().GetCamera3D();
        var companion = _world.Companion;
        var aura = companion.AuraColor ?? companion.AppearanceColor;
        var pips = _hud.PipsRow;
        GD.Print($"ALLOC minimap.Tick {Bytes(() => _hud.Map.Tick(host, _world.Player.GlobalPosition, 0.3f, companion.GlobalPosition, aura, 0.016))} B");
        GD.Print($"ALLOC camera {Bytes(() => { var vp = _hud.GetViewport(); var c = vp.GetCamera3D(); var r = vp.GetVisibleRect(); var p = c.UnprojectPosition(companion.GlobalPosition); var b = c.IsPositionBehind(companion.GlobalPosition); })} B");
        GD.Print($"ALLOC vitals {Bytes(() => { var v = host.Vitals; var r = host.BodyRules; var q = r.Quarters(v); var m = r.MaxQuarters(v); var s = host.StaminaSlowed; var mode = host.Mode; var rows = host.Rules; var a = companion.AuraColor ?? companion.AppearanceColor; })} B");
        GD.Print($"ALLOC companion {Bytes(() => { var x = companion.GlobalPosition; var h = companion.BodyHeightM; var vis = companion.Visible; var inside = companion.IsInsideTree(); var valid = GodotObject.IsInstanceValid(companion); })} B");
        GD.Print($"ALLOC read stages: export {_hud.Map.StageMs[0]:0.000} parse {_hud.Map.StageMs[1]:0.000} paint {_hud.Map.StageMs[2]:0.000} total {_hud.Map.LastBuildMs:0.000} ms, {_hud.Map.Strips} strips, {_hud.Map.InkPixels} px");
        for (var i = 0; i < 5; i++) { _hud.Map.Read(host); GD.Print($"ALLOC read {i}: export {_hud.Map.StageMs[0]:0.000} parse {_hud.Map.StageMs[1]:0.000} paint {_hud.Map.StageMs[2]:0.000} total {_hud.Map.LastBuildMs:0.000} ms"); }
        GD.Print($"ALLOC hud.Hearts.Set {Bytes(() => _hud.Hearts.Set(14, 20, false))} B");
        GD.Print($"ALLOC bar.Set {Bytes(() => _hud.Bar.Set(0.3f, 0.4f, 2f, 0.4f, false, false))} B");
        GD.Print($"ALLOC pips.SetPool {Bytes(() => pips.SetPool(5, 8, false, aura))} B");
    }

    /// <summary>A walk across the room, then what the team's map holds: the minimap's raw material and what it costs to read.</summary>
    private async Task Probe()
    {
        var host = Host;
        await Stage();
        var player = _world.Player;
        host.PlayerGoal("follow");
        var start = player.GlobalPosition;
        var ticks = new List<double>();
        for (var step = 0; step < 6; step++)
        {
            player.TryTeleportTo(start + new Vector3(0.6f * step, 0, -0.5f * step));
            player.ResetPhysicsInterpolation();
            await Frames(80);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var data = host.ExportDiscovered();
            ticks.Add(watch.Elapsed.TotalMilliseconds);
            var levels = data["levels"]!.AsArray().OfType<JsonObject>().ToList();
            var cells = levels.Sum(level => Convert.FromBase64String(level["cells"]!.GetValue<string>()).Sum(b => System.Numerics.BitOperations.PopCount(b)));
            GD.Print($"PROBE step={step} levels={levels.Count} cells_seen={cells} entities={data["entities"]!.AsObject().Count} export_ms={ticks[^1]:0.000} json_chars={data.ToJsonString().Length}");
        }
        GD.Print($"PROBE room_bounds={host.Room.Bounds} export_ms_max={ticks.Max():0.000}");
    }
}
