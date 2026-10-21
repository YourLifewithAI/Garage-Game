using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using EnFractal.Native.Characters;
using EnFractal.Native.Companion;
using EnFractal.Native.Look;
using EnFractal.Native.Look.Fauna;
using EnFractal.Native.Room;
using FileAccess = Godot.FileAccess;

namespace EnFractal.Native;

/// <summary>
/// Composition root for one room: load the room data and style preset, build the derived scene,
/// then place the player and companion at the manifest's spawns. Integrator-owned; lanes extend the
/// pieces it composes (Room/, Look/, avatars, HUD) rather than this file.
/// </summary>
public partial class RoomWorld : Node3D
{
    public const string DefaultRoom = "res://rooms/test_room";
    public const string DefaultStyleId = "storybook_painterly";
    public const int DefaultStyleVersion = 2;
    public static readonly string DefaultStyle = StylePreset.PathFor(DefaultStyleId, DefaultStyleVersion);
    /// <summary>The island's rules every room uses until a room names its own (res://rules/&lt;id&gt;/v&lt;N&gt;.json).</summary>
    public const string DefaultRulesId = "storybook_wild";
    public const int DefaultRulesVersion = 5;
    [Export] public string RoomDirectory { get; set; } = DefaultRoom;
    /// <summary>Overrides the room's pinned style when set (tests, previews).</summary>
    [Export] public string StylePresetPath { get; set; } = "";
    /// <summary>
    /// The command line the team's boot reads (--team=, --mode=, --difficulty=, --knack=); null is the game's own, the process's user
    /// arguments. The opening menu (Ui/Menu/MenuBoot.cs) sets it to its choices on top of the arguments the game was launched with,
    /// so the menu and run-room.ps1 -Team -Mode -Difficulty boot a team through one path.
    /// </summary>
    public IReadOnlyList<string>? BootArguments { get; set; }
    public RoomData Room { get; private set; } = null!;
    public LookDirector Look { get; private set; } = null!;
    public Node3D Built { get; private set; } = null!;
    public SmallPlayerController Player { get; private set; } = null!;
    public CompanionAvatar Companion { get; private set; } = null!;
    /// <summary>The shipped drawn characters (the characters round): the player's choices, and the Gubble's own.</summary>
    public CharacterLibrary Characters { get; private set; } = null!;
    /// <summary>What draws the player now: a drawn character, or null for the prototype figure.</summary>
    public CharacterVisual? PlayerVisual { get; private set; }
    /// <summary>
    /// True once the room is built and the bodies placed. The room is read on a worker thread (RoomData.LoadAsync), so a caller
    /// waits for this by the clock, never a count of frames: under --fixed-fps frames pass faster than a room loads.
    /// </summary>
    public bool WorldReady { get; private set; }
    public string LoadError { get; private set; } = "";
    /// <summary>Set when the room's pinned style could not be used and the default look was applied instead.</summary>
    public string StyleNote { get; private set; } = "";

    /// <summary>A bare room id resolves to the player's captured rooms first, then to rooms shipped with the game.</summary>
    public static string ResolveRoom(string idOrPath)
    {
        if (idOrPath.Contains('/')) return idOrPath;
        var captured = $"user://rooms/{idOrPath}";
        return FileAccess.FileExists(captured + "/room.json") ? captured : $"res://rooms/{idOrPath}";
    }

    /// <summary>An explicit override, else the room's pinned style, else the default. A pin that does not verify falls back visibly, never silently.</summary>
    private StylePreset ChooseStyle()
    {
        if (StylePresetPath.Length > 0) return StylePreset.Load(StylePresetPath);
        if (Room.DefaultStyle is { } pin)
        {
            try { return StylePreset.Resolve(pin.PresetId, pin.PresetVersion, pin.PresetSha256); }
            catch (Exception error)
            {
                StyleNote = $"Room style {pin.PresetId} v{pin.PresetVersion} is unavailable ({error.Message}); showing the default look.";
                GD.Print("LOOK: " + StyleNote);
            }
        }
        return StylePreset.Resolve(DefaultStyleId, DefaultStyleVersion);
    }

    /// <summary>The room being read on its worker thread (see _ExitTree).</summary>
    private System.Threading.Tasks.Task<RoomData>? _loading;

    /// <summary>
    /// "-- --quit-after-ready=N": quit N frames after the world is ready (exit code 0, after printing ROOM_WORLD_QUIT), or as
    /// soon as the room fails to load (exit code 1). The boot check of the runners and of a smoke boot (tools/smoke-boot.ps1):
    /// Godot's own --quit-after counts frames from the start, and frames pass faster than a room loads on its worker thread
    /// (under --fixed-fps, a few milliseconds a frame). Godot runs the C# last built, never the source: a game built before
    /// this option ignores it and never quits, so boot checks build first and look for ROOM_WORLD_QUIT (13 October).
    /// </summary>
    private static int? QuitAfterReadyFrames() =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--quit-after-ready=", StringComparison.Ordinal)) is { } argument &&
        int.TryParse(argument["--quit-after-ready=".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var frames)
            ? frames : null;

    private IReadOnlyList<CharacterChoice> _characterChoices = Array.Empty<CharacterChoice>();

    /// <summary>The shipped characters, each refusal logged; the HUD's row is built once from them (never the Gubble).</summary>
    private CharacterLibrary LoadCharacters()
    {
        var library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
        foreach (var entry in library.Entries)
            if (!entry.IsLoaded) GD.PushWarning($"CHARACTERS: {entry.Id} refused: {entry.RefusalReason}");
        // The thumbnails are the converter's renders, beside the source models (assets/characters/<id>/three_quarter.png); a
        // build without them draws a named swatch instead.
        var renders = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "assets", "characters"));
        _characterChoices = library.Characters.Where(c => c.Id != CharacterChoice.GubbleId)
            .Select(c => new CharacterChoice(c.Id, c.DisplayName, CharacterChoice.LoadThumbnail(System.IO.Path.Combine(renders, c.Id)))).ToList();
        GD.Print($"CHARACTERS: {library.Characters.Count()} loaded in {library.LoadMilliseconds:0} ms");
        return library;
    }

    /// <summary>
    /// The player chose a character ("" for the prototype figure): draw their body with it. Only a character the HUD offers is
    /// worn (never the family's Gubble); anything else, a save's or a room state's token included, draws the prototype figure.
    /// </summary>
    private void WearCharacter(string id)
    {
        var previous = PlayerVisual;
        PlayerVisual = Offered(id) ? CharacterVisual.Create(Characters, id) : null;
        if (PlayerVisual != null)
            CharacterMotion.Install(PlayerVisual, Player, () => GodotObject.IsInstanceValid(Companion) ? Companion.EyeCamera.GlobalPosition : null);
        Player.SetBodyVisual(PlayerVisual);
        if (previous != null && GodotObject.IsInstanceValid(previous.Root)) previous.Root.QueueFree();
    }

    /// <summary>Whether the HUD's row offers <paramref name="id"/> (the shipped characters but the Gubble).</summary>
    private bool Offered(string id)
    {
        foreach (var choice in _characterChoices)
            if (choice.Id == id) return true;
        return false;
    }

    public override async void _Ready()
    {
        try
        {
            // Off the main thread: a 10x island's tables are 350,000 rows to read, hash and check (S6).
            _loading = RoomData.LoadAsync(RoomDirectory);
            Room = await _loading;
            var preset = ChooseStyle();
            Look = new LookDirector { Name = "Look" };
            AddChild(Look);
            Look.Apply(preset, Room);
            Built = RoomBuilder.Build(Room);
            AddChild(Built);
            Look.Dress(Built);
            // Let PhysicsServer register every collider before placing actors on them.
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var lift = Vector3.Up * 0.008f;
            var playerSpawn = Room.SpawnFor("player");
            var companionSpawn = Room.SpawnFor("companion", playerSpawn);
            Player = new SmallPlayerController { Name = "Player", Position = playerSpawn.PositionM + lift, Rotation = new Vector3(0, Mathf.DegToRad(playerSpawn.YawDeg), 0) };
            AddChild(Player);
            Look.FocusTarget = Player;
            Player.SetSpawnPoint(Player.Position);
            Companion = new CompanionAvatar { Name = "Companion", Position = companionSpawn.PositionM + lift, Rotation = new Vector3(0, Mathf.DegToRad(companionSpawn.YawDeg), 0) };
            Companion.ConfigureIdentity("local_companion");
            AddChild(Companion);
            Companion.SetSpawnPoint(Companion.Position);
            Companion.BindPlayer(Player);
            Companion.Follow();
            // The drawn characters (docs/runs/RUN-2-CHARACTERS.md): the library, and the player's choice drawing their body. Set
            // before the HUD, which restores the saved choice when it is ready.
            Characters = LoadCharacters();
            Player.CharacterChanged = WearCharacter;
            if (GubbleCharacter.Dress(Companion, Characters, Look) is { } gubble)
                CharacterMotion.Install(gubble.Visual, Companion, () => GodotObject.IsInstanceValid(Player) ? Player.EyeCamera.GlobalPosition : null);
            // Living scenery (Run 2, Lane L): fish in the ponds and the veil under the water. Only the player frightens
            // fish; the Gubble is set apart from the world, a ghost only the player can see (the founder, 8 October).
            var pondLife = PondLife.Create(Room.RoomId, Built);
            AddChild(pondLife);
            pondLife.SetAvatars(Player);
            var hud = new RoomHud
            {
                Player = Player, Companion = Companion, RoomTitle = Room.DisplayName.ToUpperInvariant(),
                // Never a silent fallback: a style pin that did not verify, or a renderer the look was not designed for.
                LookNotice = string.Join(" ", new[] { StyleNote, Look.PlayerNotice }.Where(note => note.Length > 0)),
                Look = Look,
                CharacterChoices = () => _characterChoices,
            };
            AddChild(hud);
            // The team (M3): --team=, --mode= and --difficulty=; the saves from before teams moved under the default team; the host's
            // stores pointed into the team's folder before it exists. A test's own RoomWorld keeps the paths it set (a fixture).
            var team = Saves.TeamBoot.Prepare(this, BootArguments ?? OS.GetCmdlineUserArgs());
            var commandHost = Kernel.CommandHost.Attach(this);
            commandHost.LoadRules(DefaultRulesId, DefaultRulesVersion);
            // The team file's knacks and ranks, then the boot's one set-mode with the island save's difficulty, after which the mode is
            // fixed; a --knack= argument (quick_feet:3) only as a test's override.
            team.Apply(commandHost);
            // The team the menu made (or any team file): its costume, the Gubble's name and colour wear on the bodies, over the machine's
            // appearance profile the HUD read first. A fixture's room has no team file and keeps the profile's, as before.
            if (team.GameBoot && commandHost.Team is { } teamFile) hud.UseTeam(teamFile);
            commandHost.ConfigureNature();
            // Fire's camp (decision 49): the two bodies where this mode's island save last saw them, else at the starting campfire.
            commandHost.PlaceAtStartSpot();
            // The island's clock record once the save is read (M6): none, the machine's clock, as before.
            Look.SetWorldClockFrom(commandHost.ClockRecord);
            var natureLook = new NatureLook { Name = "NatureLook" };
            AddChild(natureLook);
            natureLook.Configure(commandHost, Built);
            // The campfires: the starting one and the firepits, unlit or lit (Fire's camp).
            AddChild(global::EnFractal.Native.Look.CampfireLook.Create(commandHost));
            // The Fractals still on the island, drawn with their glints (Run 2, L9: docs/look/FRACTALS.md).
            AddChild(global::EnFractal.Native.Look.Fractals.FractalLook.Create(commandHost, Look));
            // The hamlet's charm: smoke, warm windows, bunting, washing, lantern glow and fairy lights (Run 2, L11: docs/look/CHARM.md).
            AddChild(global::EnFractal.Native.Look.Charm.CharmLook.Create(Room, Built, Look));
            // The player's AI: the companion link in front of the command host (A2).
            CompanionBridge.Attach(this);
            // "Gubble: connected" or "waiting for your AI", for the room's first seconds (Ui/Menu/GubbleLinkNote.cs).
            Ui.GubbleLinkNote.Attach(this, hud);
            // The playtest record (R1): a windowed launch records itself; off headless, with --no-playtest-record and in a boot check.
            Playtest.PlaytestRecorder.Attach(this);
            Navigation.RoomNavigation.Attach(this);
            // The last game played, for the menu's Continue: any boot as the game (a launch with --room= too), but not a boot check.
            if (team.GameBoot && !commandHost.IslandHeld && QuitAfterReadyFrames() == null && commandHost.SaveTeamIdentity is { } who)
                new Ui.LastGame(who.TeamId, Room.RoomId, who.Mode, who.Difficulty, DateTime.UtcNow).Write(Ui.MenuRoots.Layout());
            SetMeta("room_id", Room.RoomId);
            SetMeta("room_manifest_sha256", Room.ManifestSha256);
            SetMeta("style", $"{preset.PresetId}@{preset.PresetVersion}");
            DisplayServer.WindowSetTitle($"Garage Game - {Room.DisplayName}");
            WorldReady = true;
            GD.Print($"ROOM_WORLD_READY room={Room.RoomId} shell={Room.Shell.Count} objects={Room.Objects.Count} lights={Look.RoomLightCount} style={preset.PresetId}@{preset.PresetVersion} spawn={Player.Position}");
            if (QuitAfterReadyFrames() is { } frames) QuitAfter(frames);
            // The room becoming its island (Lane L), after ready: never in a headless run, a boot check or with the player's setting off.
            else global::EnFractal.Native.Look.Intro.RoomIntro.TryStart(this);
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
            GD.PushError("Room failed to load: " + exception.Message);
            if (QuitAfterReadyFrames() != null) GetTree().Quit(1);
        }
    }

    /// <summary>
    /// Counted on the scene tree's own frame signal, not on this node, so the quit comes even if the world is freed or replaced
    /// meanwhile. The wrappers left to the finalizer are let go first: pending at exit, they can abort Godot's shutdown (the
    /// test suites do the same before they quit).
    /// </summary>
    private void QuitAfter(int frames)
    {
        var tree = GetTree();
        var left = frames;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Action? tick = null;
        tick = () =>
        {
            if (--left > 0) return;
            tree.ProcessFrame -= tick;
            GD.Print(System.FormattableString.Invariant($"ROOM_WORLD_QUIT {frames} frames after ready ({clock.Elapsed.TotalSeconds:0.0} s)"));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            tree.Quit(0);
        };
        tree.ProcessFrame += tick;
    }

    /// <summary>
    /// A room still being read when the world leaves the tree (the game quit during a load) is let finish first: its worker reads
    /// files through the engine, which crashed in FileAccess once the engine had shut down under it.
    /// </summary>
    public override void _ExitTree()
    {
        if (_loading is not { IsCompleted: false } loading) return;
        try { loading.Wait(TimeSpan.FromSeconds(60)); }
        catch (AggregateException) { /* the load's own failure is reported where it is awaited */ }
    }
}
