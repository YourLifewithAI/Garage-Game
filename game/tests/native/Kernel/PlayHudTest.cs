using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Sandbox;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The second playtest's fix round, in the real room with the real look and HUD: the diorama and isometric views are shelved
/// (F3, F4, [ and ] do nothing; the third-person view zooms out to orbit instead: PlayHudTest.Views.cs), T and Shift+T step the time of day and the season through the day's
/// own light and the solstices and equinoxes and end back on the real clock, the HUD says what the clock is, and the
/// companion's name tag is drawn solid so depth of field and temporal anti-aliasing cannot blur it. Keys are handed to the
/// HUD as the engine would hand them; nothing here needs a window, so it runs headless like the other room tests.
/// </summary>
public partial class PlayHudTest : Node
{
    /// <summary>The room's saves for this suite: the hand keys keep durable receipts, so they never touch the player's own saves.</summary>
    private const string HudSaves = "user://tests/play_hud/saves";
    /// <summary>The suite's own player profile, so it never touches the player's: written before the room boots, with the Gubble's colour 3.</summary>
    private const string HudProfile = "user://tests/play_hud/profile/avatar_profile_v1.cfg";
    private const int ProfileGubbleColour = 3;
    private int _checks;
    private int _failures;
    private RoomWorld _world = null!;
    private RoomHud _hud = null!;

    public override async void _Ready()
    {
        var previousGlowPath = CommandHost.DefaultGlowProgressPath;
        var previousVitalsPath = CommandHost.DefaultPlayerVitalsPath;
        var previousWishRoot = CommandHost.DefaultWishSaveRoot;
        try
        {
            RemoveSaves();
            CommandHost.SaveRoot = HudSaves;
            RoomHud.ProfilePath = HudProfile;
            CommandHost.DefaultGlowProgressPath = HudGlowProfile;
            CommandHost.DefaultPlayerVitalsPath = HudProfile.GetBaseDir() + "/player_vitals_v1.json";
            CommandHost.DefaultWishSaveRoot = HudSaves + "/wishes";
            WriteProfile(new Godot.Collections.Dictionary { ["version"] = 1, ["player_color"] = 0, ["companion_color"] = ProfileGubbleColour, ["companion_name"] = "the Gubble" });
            _world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(_world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_world.WorldReady && _world.LoadError.Length == 0;) await Frames(1);
            Check(_world.WorldReady, "the room boots with its HUD and look: " + _world.LoadError);
            _hud = _world.GetNode<RoomHud>("RoomHud");
            await Frames(10);
            GD.Print($"PLAY_HUD_INFO: physics interpolation {GetTree().PhysicsInterpolation}, TAA {GetViewport().UseTaa}, screen-space AA {GetViewport().ScreenSpaceAA}");

            await TestNoFinalizerGarbage();
            await TestGlowJournalFresh();
            await TestMagicPanel();
            await TestFoldedHelp();
            // Notices where the eye is: under the reticle, fading after about four seconds (PlayHudTest.Notice.cs).
            await TestNotices();
            // The card is in the help and the full-screen key (PlayHudTest.Screen.cs); the swim hint is with the views, which move the body.
            await TestCardIsInTheHelp();
            TestFullscreenAtBoot();
            await TestFullscreenKey();
            TestWorkshopGone();
            await TestTimeOfDay();
            await TestSeason();
            await TestClockCombined();
            await TestNoLookNoClock();
            TestNameTag();
            await TestNameTagSize();
            await TestControlsMap();
            await TestHelpFollowsMap();
            await TestRemappedKeys();
            await TestHandKeys();
            await TestGubbleReady();
            // The three meters and the minimap (PlayHudTest.Meters.cs): after the Gubble waits beside the player, before its keys.
            TestMeterMath();
            await TestHeartsAndBar();
            await TestMeterNumbers();
            await TestPoolAndPrices();
            await TestAskTagPrice();
            await TestMinimap();
            // The follow-up (PlayHudTest.Spend.cs): the pips under the bar when the Gubble is out of frame, Fractals spent through the journal.
            await TestPipsMarker();
            await TestSpendFractalsPage();
            await TestSpendHint();
            // The journal's book pulses when five Fractals are in hand with a spend to be made (decision 53).
            await TestJournalPulse();
            await TestMeterFrameCost();
            await TestMarkerFrameCost();
            TestAuraFromProfile();
            TestDuskLogic();
            await TestDuskInRoom();
            await TestGubbleKeys();
            await TestSmartAsk();
            // The Bubble by the smart ask (PlayHudTest.Bubble.cs): choose it, lead it, lift it, let it go.
            await TestBubbleAsk();
            await TestWheel();
            await TestAcknowledgement();
            await TestReadableWords();
            await TestLookDrag();
            await TestGlowWherePointed();
            await TestPutDown();
            await TestAuraRecolours();
            await TestProfileField();
            await TestJournalBook();
            await TestExpertiseWords();
            await TestRenamedWords();
            await TestEyesSetting();
            // The settings panel that took the Customize panel's switches (PlayHudTest.Settings.cs), and Reset Trust beside Change my AI.
            await TestSettingsPanel();
            // Esc is the menu (PlayHudTest.Pause.cs): it pauses, cancels what is going on first, saves, loads, settings, the main menu.
            await TestPauseMenu();
            await TestGubbleView();
            await TestCharacterChoice();
            await TestWearingCharacters();
            await TestDrawnGubble();
            await TestGlowJournalLearned();
            await TestOtherExpertiseJournal();
            // A held island's pages say it is paused (decision 54).
            await TestIslandPausedWords();
            // The two views last: they move the player about the room and leave the Gubble where it was.
            await TestShelvedViewKeys();
            await TestShoulderRange();
            await TestShoulderOrbit();
            await TestShoulderMovementFrame();
            await TestShoulderNearEndUnchanged();
            await TestShoulderKeepsTheBodyInFrame();
            await TestShoulderFocusAndSwitches();
            await TestSwimHint();
            // Fire's camp: the rest key and the expertise announcement (PlayHudTest.Camp.cs).
            await TestCampHud();
            // E, the one interaction key, and its prompt (PlayHudTest.Interact.cs).
            await TestInteractKey();
            // The camp's screen: sitting opens it (PlayHudTest.CampScreen.cs).
            await TestCampScreen();
            // The Fire behaviours packet (PlayHudTest.FireBehaviours.cs): Fractals collected by touch, the Magic panel's key, Fire on the wheel.
            await TestFractalTouch();
            await TestMagicKey();
            await TestFireWheel();
            // The playtest record's keys (PlayHudTest.Playtest.cs).
            await TestPlaytestKeys();

            // Unload while the final cast's chime is still active. AudioServer
            // retires stopped playback on subsequent frames, before engine exit.
            // Keep the isolated save paths installed throughout world teardown.
            _world.Free();
            var audioDeadline = Time.GetTicksMsec() + 200;
            while ((HudAudioResourceAlive(_lastGlowPlaybackId) || HudAudioResourceAlive(_lastGlowVoiceId)) && Time.GetTicksMsec() < audioDeadline)
            {
                // The mixer uses real time even when --fixed-fps advances test
                // frames quickly. Stop must retire it before the 250 ms tone ends.
                await Task.Delay(5);
                await Frames(1);
            }
            Check(!HudAudioResourceAlive(_lastGlowPlaybackId) && !HudAudioResourceAlive(_lastGlowVoiceId),
                "unloading the world releases its stopped chime playback and generated sound before engine exit");
            // The journal across boots, in this suite's saves: a note survives a reload; a team that has seen nothing reads nothing.
            await TestJournalAfterReload();
            await TestJournalNothingUnseen();
            await TestFirstNoticeIsEmpty();
            await TestGatherKey();
            CommandHost.SaveRoot = CommandHost.DefaultSaveRoot;
            RoomHud.ProfilePath = RoomHud.DefaultProfilePath;
            CommandHost.DefaultGlowProgressPath = previousGlowPath;
            CommandHost.DefaultPlayerVitalsPath = previousVitalsPath;
            CommandHost.DefaultWishSaveRoot = previousWishRoot;
            RemoveSaves();
            GD.Print($"NATIVE_KERNEL_PLAY_HUD: {_checks - _failures}/{_checks} checks passed; every key is a named input action with the Glow round's defaults and the help reads them (a remapped key changes both what it does and what the help shows), H folds the help, F3, F4, [ and ] do nothing while the wheel zooms the third-person view out to an orbit with movement following it, T and Shift+T step the time of day and the season back to the real clock, the companion's solid name tag stays small and hides near the camera, F and V pick up, carry, set down on the box and push, the right button's tap asks what fits the frozen aim and its hold opens the wheel, 1 casts Glow, Q recalls, X stops, the Gubble's bubble answers with the host's result, its glow takes its colour, the dusk moment teaches Glow once, and J or a click opens the journal book at its index, written from the host's data, holding the game until it is back in its dock, and I or the eye icon above it looks through the Gubble's eyes, live, its eye turned by a drag within the AI's bounds, holding the game and changing nothing, the icon blinking when the AI looks");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            CommandHost.DefaultGlowProgressPath = previousGlowPath;
            CommandHost.DefaultPlayerVitalsPath = previousVitalsPath;
            CommandHost.DefaultWishSaveRoot = previousWishRoot;
            GD.PushError("Play HUD test exception: " + error);
            GetTree().Quit(1);
        }
    }

    private static InputEventKey Press(Key key, bool shift = false) => new() { PhysicalKeycode = key, Pressed = true, ShiftPressed = shift };

    // ---- The controls are input actions (game/project.godot, [input]) ----

    /// <summary>
    /// Today's bindings (the magic design's "The keys", step 2), written out here on purpose: the map must hold exactly these until
    /// the founder changes a default. Enter is kept for the wish box: nothing is on it.
    /// </summary>
    private static readonly (string Action, Key Key, bool Shift)[] DefaultKeys =
    {
        ("move_forward", Key.W, false), ("move_back", Key.S, false), ("move_left", Key.A, false), ("move_right", Key.D, false),
        ("move_sprint", Key.Shift, false), ("move_dive", Key.Ctrl, false), ("jump", Key.Space, false),
        ("body_recover", Key.R, false), ("body_home", Key.B, false),
        ("view_eye", Key.F1, false), ("view_shoulder", Key.F2, false), ("view_observe", Key.O, false),
        // The window's own key (decision 66, 16 October): F11 was bound to nothing.
        ("view_fullscreen", Key.F11, false),
        ("gubble_recall", Key.Q, false), ("gubble_stop", Key.X, false),
        ("gubble_slot_1", Key.Key1, false), ("gubble_slot_2", Key.Key2, false), ("gubble_slot_3", Key.Key3, false),
        ("gubble_slot_4", Key.Key4, false), ("gubble_slot_5", Key.Key5, false),
        // Fire on the wheel (decision 77): 6; and the Magic panel's own key, M (it was only a button inside H).
        ("gubble_slot_6", Key.Key6, false), ("hud_magic", Key.M, false),
        ("hands", Key.F, false), ("push", Key.V, false), ("item_gather", Key.E, false),
        // Fire's camp: sit down by a lit campfire (decision 70).
        ("rest", Key.Z, false),
        ("physics_next", Key.G, false), ("time_step", Key.T, false), ("season_step", Key.T, true), ("lamps", Key.L, false),
        ("bounce_step", Key.P, false), ("bounce_print", Key.P, true),
        ("hud_help", Key.H, false), ("hud_customize", Key.C, false), ("journal", Key.J, false), ("gubble_eyes", Key.I, false), ("mouse_release", Key.Escape, false),
        // The playtest record's keys (docs/runs/RUN-2-PLAYTEST-LOOP.md): F6 to F9, bound to nothing in Windows.
        ("playtest_mark", Key.F9, false), ("playtest_keep", Key.F8, false), ("playtest_stills_faster", Key.F7, false), ("playtest_stills_slower", Key.F6, false),
    };

    private readonly System.Collections.Generic.Dictionary<string, Godot.Collections.Array<InputEvent>> _savedMap = new();

    /// <summary>The map as it was, so a test that remaps keys leaves every later test the defaults.</summary>
    private void SaveMap()
    {
        _savedMap.Clear();
        foreach (var action in PlayerControls.MapActions()) _savedMap[action] = InputMap.ActionGetEvents(action);
    }

    private void RestoreMap()
    {
        foreach (var (action, events) in _savedMap)
        {
            InputMap.ActionEraseEvents(action);
            foreach (var input in events) InputMap.ActionAddEvent(action, input);
        }
        // Whoever binds keys says so: the HUD's key names are read from the map again at once (PlayerControls.KeyLabel).
        PlayerControls.MapChanged();
    }

    private static void Bind(StringName action, Key key, bool shift = false)
    {
        InputMap.ActionEraseEvents(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key, ShiftPressed = shift });
        PlayerControls.MapChanged();
    }

    /// <summary>What an action's key is, as a comparable string: the kind, the key and the modifiers it asks for.</summary>
    private static string Signature(string action)
    {
        var events = InputMap.ActionGetEvents(action);
        if (events.Count != 1) return $"{events.Count} events";
        return events[0] switch
        {
            InputEventKey key => $"key {key.PhysicalKeycode}/{key.Keycode} mods {key.GetModifiersMask()}",
            InputEventMouseButton mouse => $"mouse {mouse.ButtonIndex} mods {mouse.GetModifiersMask()}",
            var other => other.GetType().Name,
        };
    }

    /// <summary>Pairs of actions live in the same context on one key. A nested pair (Shift+T over T) is allowed only when the code checks the longer one first.</summary>
    private static System.Collections.Generic.List<string> KeyConflicts()
    {
        var found = new System.Collections.Generic.List<string>();
        var contexts = new (string Name, ControlContext Live, ControlContext Extra)[]
        {
            ("always", ControlContext.Always, 0), ("F2", ControlContext.Always, ControlContext.Shoulder),
            ("customise", ControlContext.Customize, 0),
            ("reading", ControlContext.Reading, 0), ("looking", ControlContext.Looking, 0),
        };
        foreach (var (name, live, extra) in contexts)
        {
            var specs = PlayerControls.Specs.Where(spec => (spec.Live & (live | extra)) != 0).ToArray();
            for (var i = 0; i < specs.Length; i++)
                for (var j = i + 1; j < specs.Length; j++)
                {
                    var (a, b) = (specs[i].Action, specs[j].Action);
                    var (ka, kb) = (InputMap.ActionGetEvents(a).FirstOrDefault(), InputMap.ActionGetEvents(b).FirstOrDefault());
                    if (ka == null || kb == null) { found.Add($"{name}: {(ka == null ? a : b)} has no key"); continue; }
                    if (Same(ka, kb) is not { } relation) continue;
                    // relation 0: same key and modifiers; 1: a's modifiers contain b's; -1: b's contain a's.
                    var allowed = relation switch
                    {
                        1 => PlayerControls.CheckedBefore.Any(pair => pair.First == a && pair.Second == b),
                        -1 => PlayerControls.CheckedBefore.Any(pair => pair.First == b && pair.Second == a),
                        _ => false,
                    };
                    if (!allowed) found.Add($"{name}: {a} and {b} share a key");
                }
        }
        return found;
    }

    /// <summary>Null when the two are different keys; else how their modifiers relate (see KeyConflicts).</summary>
    private static int? Same(InputEvent a, InputEvent b)
    {
        (long Code, ulong Mods)? Of(InputEvent input) => input switch
        {
            InputEventKey key => (((long)(key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode)), (ulong)key.GetModifiersMask()),
            InputEventMouseButton mouse => (-1000L - (long)mouse.ButtonIndex, (ulong)mouse.GetModifiersMask()),
            _ => null,
        };
        if (Of(a) is not { } x || Of(b) is not { } y || x.Code != y.Code) return null;
        if (x.Mods == y.Mods) return 0;
        if ((x.Mods & y.Mods) == y.Mods) return 1;
        return (x.Mods & y.Mods) == x.Mods ? -1 : null;
    }

    private async Task TestControlsMap()
    {
        SaveMap();
        try
        {
            // The map holds today's keys exactly, and nothing else but the mouse's four.
            var wrong = DefaultKeys.Where(row =>
                InputMap.ActionGetEvents(row.Action) is not { Count: 1 } events || events[0] is not InputEventKey key ||
                key.PhysicalKeycode != row.Key || key.Keycode != Key.None || key.GetModifiersMask() != (row.Shift ? KeyModifierMask.MaskShift : 0)).Select(row => row.Action).ToArray();
            Check(wrong.Length == 0, "every key action is bound to today's physical key, with only the modifiers it had: " + string.Join(", ", wrong));
            Check(Signature("mouse_capture") == "mouse Left mods 0" && Signature("view_zoom_in") == "mouse WheelUp mods 0" && Signature("view_zoom_out") == "mouse WheelDown mods 0",
                "the left button is the look drag and the wheel zooms: both are actions too");
            Check(Signature("gubble_ask") == "mouse Right mods 0", "the right button is the Gubble's ask (tap) and wheel (hold): " + Signature("gubble_ask"));
            Check(!PlayerControls.MapActions().Any(a => InputMap.ActionGetEvents(a).Any(e => e is InputEventKey { PhysicalKeycode: Key.Enter or Key.KpEnter } || e is InputEventKey { Keycode: Key.Enter or Key.KpEnter })),
                "Enter is kept free for the wish box: no action is on it");
            var mapped = PlayerControls.MapActions().ToArray();
            var named = DefaultKeys.Select(row => row.Action).Concat(new[] { "mouse_capture", "view_zoom_in", "view_zoom_out", "gubble_ask" }).Order().ToArray();
            Check(mapped.SequenceEqual(named), $"the map holds exactly the {named.Length} controls (no more, no fewer): {string.Join(", ", mapped.Except(named).Concat(named.Except(mapped)))}");
            var specs = PlayerControls.Specs.Select(spec => spec.Action.ToString()).Order().ToArray();
            var constants = typeof(Act).GetFields().Where(f => f.FieldType == typeof(StringName)).Select(f => f.GetValue(null)!.ToString()!).Order().ToArray();
            Check(specs.SequenceEqual(mapped) && constants.SequenceEqual(mapped), "the map, the list of controls and the action names in code are the same set");

            // The code asks for the map's actions and for no others: record what the two handlers ask while a key that is none of
            // them goes through the HUD (in F2, where the zoom keys are live) and the body, which also polls its held keys.
            var player = _world.Player;
            PlayerControls.Trace = new System.Collections.Generic.HashSet<string>();
            _hud.SetViewMode(1);
            _hud._UnhandledInput(Press(Key.Backslash));
            player._UnhandledInput(Press(Key.Backslash));
            await Frames(4);
            var asked = PlayerControls.Trace;
            PlayerControls.Trace = null;
            _hud.SetViewMode(1);
            Check(!asked.Except(mapped).Any(), "every action the code asks for is in the map: " + string.Join(", ", asked.Except(mapped)));
            Check(!mapped.Except(asked).Any(), "every action in the map is asked for by the code: " + string.Join(", ", mapped.Except(asked)));

            // No two keys of a context are one key.
            var conflicts = KeyConflicts();
            Check(conflicts.Count == 0, "no two actions share a key in the same context (always, F2's wheel, the appearance panel): " + string.Join("; ", conflicts));
            Bind(Act.Push, Key.F);
            Check(KeyConflicts().Any(c => c.Contains("hands", StringComparison.Ordinal) && c.Contains("push", StringComparison.Ordinal)), "and the check does see two actions put on one key");
            RestoreMap();
            Bind(Act.ViewZoomIn, Key.F);
            Check(KeyConflicts().Any(c => c.StartsWith("F2", StringComparison.Ordinal)) && !KeyConflicts().Any(c => c.StartsWith("always", StringComparison.Ordinal)),
                "a key two contexts use for different things is no conflict: F2's zoom on the hands' key conflicts in F2 only");
            RestoreMap();
            Check(KeyConflicts().Count == 0, "the defaults are back");

            // A synthetic key event that names only the key's meaning (Keycode), not its place, is read as the place, as it was before the actions.
            var folded = _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible;
            _hud._UnhandledInput(new InputEventKey { Keycode = Key.H, Pressed = true });
            var unfolded = _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible;
            _hud._UnhandledInput(new InputEventKey { Keycode = Key.H, Pressed = true });
            Check(!folded && unfolded && !_hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible,
                "an event with only a Keycode (no physical key) still presses the key it names");
        }
        finally { RestoreMap(); PlayerControls.Trace = null; }
    }

    /// <summary>The key help, as it reads now: the hint, then each help line in order (the buttons are read separately).</summary>
    private string[] HelpLines()
    {
        var column = _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0);
        return new[] { column.GetChild<Label>(0).Text }.Concat(column.GetNode<VBoxContainer>("KeyHelp").GetChildren().OfType<Label>().Select(l => l.Text)).ToArray();
    }

    private string[] ButtonTexts(string row) => _hud.FindChild(row, true, false)!.GetChildren().OfType<Button>().Select(b => b.Text).ToArray();

    /// <summary>The help is built from the map: the same words as before for the default keys, and a remapped key shows in them.</summary>
    private async Task TestHelpFollowsMap()
    {
        SaveMap();
        try
        {
            var home = _world.Player.HomeName;
            var fireLearned = CommandHost.Of(_world)!.FireLearned;
            await Frames(2);
            var expected = new[]
            {
                "H keys",
                // E first (decision 86: the help names it first).
                "E uses what is in front of you: sits you by a lit campfire and stands you up again, picks up a fractal or a thing, sets down what you hold, gathers a log or stone you hold into your wallet · Z sits you by the fire too, this round (moving stands you up)",
                $"WASD move · Shift run · Space jump · B back to {home} · R recover · drag to look around",
                "Climb: keep walking into a steep face, W up, S down, A/D across, Space lets go · Swim: deep water floats you, Space leaps · Dive: hold Ctrl, Space rises, W swims the way you look, let go to drift up",
                "F1 first person · F2 third person: wheel zooms out to look around, and zoomed out drag orbits and WASD follows the view",
                "O observe (a very tight tilt-shift view, best zoomed out) · C settings · M the Gubble's magic · J journal (Esc closes any)",
                "F11 full screen on or off (kept for your next launch)",
                "Walk into a fractal to collect it · F pick up what you face · F again sets it down in front of you, or on top of what you face (the box, the book) · V push what you face 10 cm",
                "The Gubble, your companion:",
                "right-click asks for what fits where you point (wait or follow, fetch or put down, light the dark, go and look; on a bubble, choose it, then a spot to lead it there, a higher one to lift it); hold it for the wheel · Q come, then follow · X stop · I look through its eyes",
                $"1 Glow{PriceWords("glow")}, 2 Grow{PriceWords("grow")}, 3 Bubbles{PriceWords("bubbles")}, 4 Fireworks{PriceWords("fireworks")}, 6 Fire" +
                (fireLearned ? $"{PriceWords("fire")} where you point (it comes closer first if it must; at the sky, on itself) · 6 lights Fire where you point, at once (it burns until it burns out, or Snuff puts it out)"
                    : " (locked: rest by the starting campfire first) where you point (it comes closer first if it must; at the sky, on itself)") +
                " · 5 magic still to come · it floats after you, over water and up cliffs",
                "Testing",
                "G gravity · T time of day · Shift+T season (each steps round to the real clock) · L lamps · P your character's bounce (x0.5, x1, x1.5) · Shift+P print its motion profile",
                "Playtest record: F9 something's off here · F8 keep this · F7 stills faster · F6 slower (the record is off for this run)",
            };
            var lines = HelpLines();
            Check(lines.SequenceEqual(expected), "with the default keys the help says drag where it said click, groups the Gubble's keys (its ask, Q, X, 1 to 5) under its name, and puts the testing toggles (G, T, Shift+T, L) under a \"Testing\" heading:\n" + string.Join("\n", lines.Except(expected)));
            Check(ButtonTexts("CompanionActions").SequenceEqual(new[] { "Follow", "Wait", "Q Come", "X Stop", "1 Glow", "Settings", "Magic" }) &&
                ButtonTexts("HandActions").SequenceEqual(new[] { "E Use", "F Pick up / put down", "V Push" }), "and so do the buttons in the help");
            Check(((Label)_hud.FindChild("CompanionName", true, false)!).Text == "The Gubble:", "and the Gubble's buttons stand under its name");
            var state = ((Label)_hud.FindChild("State", true, false)!).Text;
            Check(state.Contains(" (G)", StringComparison.Ordinal) && _hud.ClockText().Contains("real clock (T)", StringComparison.Ordinal) && _hud.ClockText().Contains("real date (Shift+T)", StringComparison.Ordinal),
                $"and the state and clock lines name G, T and Shift+T: {state} | {_hud.ClockText()}");

            // Every control on a key of its own that is in no other word of the help: each shows its own key, and the old keys are gone.
            var pool = new[]
            {
                Key.F13, Key.F14, Key.F15, Key.F16, Key.F17, Key.F18, Key.F19, Key.F20, Key.F21, Key.F22, Key.F23, Key.F24,
                Key.Kp0, Key.Kp1, Key.Kp2, Key.Kp3, Key.Kp4, Key.Kp5, Key.Kp6, Key.Kp7, Key.Kp8, Key.Kp9,
                Key.KpAdd, Key.KpSubtract, Key.KpMultiply, Key.KpDivide, Key.KpPeriod, Key.KpEnter, Key.Insert, Key.Delete, Key.Pause, Key.Pageup, Key.Pagedown,
                Key.Home, Key.End, Key.Numlock, Key.Scrolllock, Key.Sysreq, Key.F25, Key.F26, Key.F27, Key.F28, Key.F29, Key.F30, Key.F31, Key.F32,
            };
            var actions = PlayerControls.Specs.Select(spec => spec.Action).ToArray();
            Check(pool.Length >= actions.Length, "enough spare keys to give every control its own");
            var own = actions.Select((action, i) => (Action: action, Key: pool[i], Name: OS.GetKeycodeString(pool[i]))).ToArray();
            foreach (var (action, key, _) in own) Bind(action, key);
            await Frames(2);
            var text = string.Join("\n", HelpLines());
            var missing = own.Where(o => !text.Contains(o.Name, StringComparison.Ordinal)).Select(o => $"{o.Action} ({o.Name})").ToArray();
            Check(missing.Length == 0, $"the help lists every one of the {own.Length} controls with its current key: missing {string.Join(", ", missing)}");
            Check(!text.Contains("WASD", StringComparison.Ordinal) && !text.Contains("Shift+T", StringComparison.Ordinal) && !text.Contains("Esc", StringComparison.Ordinal) && !text.Contains("click", StringComparison.Ordinal),
                "and none of the old keys is left in it");
            Check(ButtonTexts("CompanionActions")[2] == $"{own.First(o => o.Action == Act.GubbleRecall).Name} Come", "the help's buttons follow the map too");
            Check(_hud.DuskHintLabel.Text == $"{own.First(o => o.Action == Act.GubbleSlot1).Name} or {own.First(o => o.Action == Act.GubbleAsk).Name}: Light!", "and so does the dusk hint: " + _hud.DuskHintLabel.Text);
            Check(SandboxControls.FocusWords.PickUpOrPush == $"{own.First(o => o.Action == Act.Hands).Name} pick up · {own.First(o => o.Action == Act.Push).Name} push" &&
                string.Format(SandboxControls.FocusWords.SetOn, "A", "B") == $"{own.First(o => o.Action == Act.Hands).Name} set A on B",
                "and so do the focus tag's words (the hand keys F and V were written into them)");
            var ownLabels = actions.Select(a => (Action: a, Label: PlayerControls.Label(a))).ToArray();
            Check(own.All(o => ownLabels.First(l => l.Action == o.Action).Label == o.Name), "Label(action) is the action's current key, named as the engine names it");
            RestoreMap();
            await Frames(2);
            Check(HelpLines().SequenceEqual(expected), "and back to the default keys, the help reads as before");
            Bind(Act.SeasonStep, Key.Y, shift: true);
            Check(PlayerControls.Label(Act.SeasonStep) == "Shift+Y", "a key with a modifier is named with it");
        }
        finally { RestoreMap(); }
    }

    /// <summary>Remapping an action changes what the key does (the old key goes dead) and what the help says, in the contexts it is live in.</summary>
    private async Task TestRemappedKeys()
    {
        SaveMap();
        var companion = _world.Companion;
        try
        {
            // A companion key: the default does it, then the same action on another key does it and the old key does not.
            _hud._UnhandledInput(Press(Key.Q));
            Check(companion.CurrentIntent == "come", "Q calls the Gubble (come)");
            _hud._UnhandledInput(Press(Key.X));
            Check(companion.CurrentIntent == "stop", "X stops it");
            _hud._UnhandledInput(Press(Key.Q));
            // (J is the journal's since the journal book: the remapped stop goes on K.)
            Bind(Act.GubbleStop, Key.K);
            await Frames(2);
            _hud._UnhandledInput(Press(Key.X));
            Check(companion.CurrentIntent != "stop", "with stop on K, X does nothing");
            _hud._UnhandledInput(Press(Key.K));
            Check(companion.CurrentIntent == "stop", "K stops it");
            var echo = Press(Key.Q); echo.Echo = true;
            _hud._UnhandledInput(echo);
            Check(companion.CurrentIntent == "stop", "a held key repeating (echo) does not count as a press");
            Check(HelpLines().Any(l => l.Contains("Q come, then follow · K stop", StringComparison.Ordinal)) && ButtonTexts("CompanionActions")[3] == "K Stop",
                "and the help and the button say K stop");
            RestoreMap();

            // Time of day: T moves to Y, and Shift+T is still the season.
            var look = _world.Look;
            Bind(Act.TimeStep, Key.Y);
            await Frames(2);
            _hud._UnhandledInput(Press(Key.T));
            Check(_hud.TimeStop == -1 && _hud.SeasonStop == -1, "with time of day on Y, T steps nothing");
            _hud._UnhandledInput(Press(Key.Y));
            Check(_hud.TimeStop == 0 && _hud.SeasonStop == -1, "Y steps the time of day");
            _hud._UnhandledInput(Press(Key.T, shift: true));
            Check(_hud.TimeStop == 0 && _hud.SeasonStop == 0, "Shift+T is still the season");
            Check(HelpLines().Any(l => l.Contains("Y time of day · Shift+T season", StringComparison.Ordinal)) && _hud.ClockText().Contains("dawn (Y)", StringComparison.Ordinal),
                "and the help and the clock line say Y");
            for (var i = 0; i < RoomHud.TimeStopNames.Length; i++) _hud._UnhandledInput(Press(Key.Y));
            for (var i = 0; i < RoomHud.SeasonStops.Length; i++) _hud._UnhandledInput(Press(Key.T, shift: true));
            await Frames(2);
            Check(_hud.TimeStop == -1 && _hud.SeasonStop == -1 && look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal), "both stepped round to the real clock again");
            RestoreMap();

            // The wheel zooms F2 only (F1 has nothing to zoom); moved to keys it zooms the same way, and the wheel goes quiet.
            Func<MouseButton, InputEventMouseButton> wheel = which => new InputEventMouseButton { ButtonIndex = which, Pressed = true };
            _hud.SetViewMode(0);
            var distance = _hud.ShoulderDistanceM;
            _hud._UnhandledInput(wheel(MouseButton.WheelDown));
            Check(Mathf.IsEqualApprox(distance, _hud.ShoulderDistanceM), "the wheel zooms nothing in F1");
            _hud.SetViewMode(1);
            _hud.ZoomShoulder(-6);
            distance = _hud.ShoulderDistanceM;
            _hud._UnhandledInput(wheel(MouseButton.WheelUp));
            var closer = _hud.ShoulderDistanceM;
            _hud._UnhandledInput(wheel(MouseButton.WheelDown));
            Check(closer < distance - 0.01f && Mathf.IsEqualApprox(distance, _hud.ShoulderDistanceM), $"in F2 the wheel zooms in and out ({distance:0.00} to {closer:0.00} m and back)");
            Bind(Act.ViewZoomIn, Key.U);
            await Frames(2);
            _hud._UnhandledInput(wheel(MouseButton.WheelUp));
            Check(Mathf.IsEqualApprox(distance, _hud.ShoulderDistanceM), "with zoom-in on U the wheel up zooms nothing");
            _hud._UnhandledInput(Press(Key.U));
            Check(_hud.ShoulderDistanceM < distance - 0.01f, "U zooms in");
            Check(HelpLines().Any(l => l.Contains("U/wheel down zooms out", StringComparison.Ordinal)), "and the help names both zoom keys");
            RestoreMap();
            var zoomed = _hud.ShoulderDistanceM;
            _hud._UnhandledInput(wheel(MouseButton.WheelUp));
            Check(_hud.ShoulderDistanceM < zoomed - 0.01f, "the wheel zooms again with the defaults back");
            _hud.ZoomShoulder(1000);
            Check(Mathf.IsEqualApprox(_hud.ShoulderDistanceM, _hud.ShoulderNearM), "(the zoom put back at the shoulder)");

            // Esc and the appearance panel: the panel blocks every key but its own two.
            Bind(Act.HudCustomize, Key.K);
            _hud._UnhandledInput(Press(Key.C));
            Check(!_hud.SettingsOpen, "with the panel on K, C opens nothing");
            _hud._UnhandledInput(Press(Key.K));
            Check(_hud.SettingsOpen, "K opens the panel");
            var before = _hud.ViewMode;
            _hud._UnhandledInput(Press(Key.F1));
            Check(_hud.ViewMode == before, "and with it open the view keys do nothing");
            _hud._UnhandledInput(Press(Key.Escape));
            Check(!_hud.SettingsOpen, "Esc closes the panel");
            RestoreMap();
        }
        finally { RestoreMap(); _hud.SetViewMode(1); if (_hud.SettingsOpen) _hud._UnhandledInput(Press(Key.C)); }
    }

    /// <summary>
    /// P3's acceptance in the real room through the HUD's keys, the same commands the companion sends: the player picks up the
    /// doorstop (F), carries it across the room to the big box and sets it on top (F, a durable receipt), pushes it along the
    /// box (V twice), and is told plainly when what it faces is too heavy to pick up.
    /// </summary>
    private async Task TestHandKeys()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        player.ReadKeyboard = false;
        _hud.SetViewMode(1);
        var notice = (Label)_hud.FindChild("Notice", true, false)!;
        var state = (Label)_hud.FindChild("State", true, false)!;
        var help = string.Join("\n", _world.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text));
        Check(help.Contains("F pick up", StringComparison.Ordinal) && help.Contains("V push", StringComparison.Ordinal), "the help names the hand keys F and V");
        Check(_hud.FindChild("HandActions", true, false) is HBoxContainer { } buttons && buttons.GetChildCount() == 3, "and the help has their buttons (E use, pick up, push)");
        // The founder's name for the companion (8 October): the Gubble, in its name tag, the HUD's state line and the help.
        await Frames(1);
        var tag = _world.Companion.GetNode<Label3D>("CompanionLabel").Text;
        Check(_world.Companion.CompanionName == CompanionAvatar.DefaultName && tag == "The Gubble" && state.Text.Contains("the Gubble: ", StringComparison.Ordinal) &&
            help.Contains("the Gubble", StringComparison.Ordinal) && !help.Contains("Wisp", StringComparison.Ordinal),
            $"the companion is the Gubble in its name tag ({tag}), the state line and the help ({state.Text})");
        Check(_hud.FindChild("HomeVeil", true, false) is ColorRect { Color.A: 0, MouseFilter: Control.MouseFilterEnum.Ignore },
            "the veil of the way home is in place, clear and letting clicks through while nobody is going home");
        Check(help.Contains("B back to the start", StringComparison.Ordinal),
            "the help lists B, home from anywhere, all the time (a room without a sea: back to the start)");
        Check(CompanionAvatar.SavedName("Wisp") == "the Gubble" && CompanionAvatar.SavedName("Pip") == "Pip",
            "a profile or save still naming the companion Wisp (the former default) restores it as the Gubble; a name the player chose stays");
        Check(await WalkTo(player, new Vector2(-0.3f, 0.62f)), "the player walks over to the doorstop on the rug");
        // The focus (RUN-2-OPEN-SEA.md, "Things to touch"): facing away, nothing lit; facing the doorstop, it is lit, and its
        // tag beside it names the keys, never in the top-right corner kept for the minimap.
        await Face(player, Vector3.Back);
        await Frames(40);
        var look = _hud.Look;
        Check(_hud.FocusTarget == null && !_hud.FocusTag.Visible && (look == null || look.Highlight.Target == null),
            "with nothing the hand keys would act on in front, nothing is lit and no tag shows");
        await Face(player, Vector3.Forward);
        await Frames(40);
        var tagRect = new Rect2(_hud.FocusTag.Position, _hud.FocusTag.Size);
        var viewport = _hud.GetViewport().GetVisibleRect().Size;
        var inCorner = tagRect.End.X > viewport.X - SandboxControls.FocusWords.MinimapWidthPx && tagRect.Position.Y < SandboxControls.FocusWords.MinimapHeightPx;
        Check(_hud.FocusTarget?.GetMeta("entity_id", "").AsString() == "obj:doorstop" && (look == null || look.Highlight.Target == _hud.FocusTarget) &&
              _hud.FocusTag.Visible && _hud.FocusTag.Text == "E: pick up Doorstop · V push" && _hud.Interaction.Kind == RoomHud.InteractKind.PickUp && !inCorner,
            $"facing the doorstop it is lit ({(look == null ? "no look in this run" : "the look's highlight on it")}), its tag \"{_hud.FocusTag.Text}\" at {tagRect.Position}, clear of the minimap's corner");
        var queries = _hud.FocusQueries;
        await Frames(60);
        Check(_hud.FocusQueries - queries <= 3, $"standing still, the focus is asked for at most every half second ({_hud.FocusQueries - queries} times in 1 s)");
        _hud._UnhandledInput(Press(Key.F));
        await Frames(2);
        Check(host.HeldBy(CommandHost.PlayerAvatar) == "obj:doorstop" && notice.Text.StartsWith("Holding Doorstop", StringComparison.Ordinal) && state.Text.Contains("holding Doorstop (F)", StringComparison.Ordinal) &&
              notice.Text.Contains($"{PlayerControls.Label(Act.Hands)} sets it down", StringComparison.Ordinal),
            $"F picks up the doorstop it faces; the HUD says so ({notice.Text} | {state.Text})");
        var revision = host.Revision;
        Check(await WalkTo(player, new Vector2(0.87f, 0.17f)), "the player carries it across the room to the big box");
        await Face(player, Vector3.Right);
        Check(Entity(host, "obj:doorstop")["held_by"]?.GetValue<string>() == CommandHost.PlayerAvatar, "still holding it on arrival");
        await Frames(40);
        Check(_hud.FocusTarget?.GetMeta("entity_id", "").AsString() == "obj:box" && _hud.FocusTag.Text == "E: set Doorstop on Cardboard box" && _hud.Interaction.Kind == RoomHud.InteractKind.SetOn,
            $"carrying it, facing the box, the box is lit, where F would set it: \"{_hud.FocusTag.Text}\" on {_hud.FocusTarget?.GetMeta("entity_id", "")}");
        _hud._UnhandledInput(Press(Key.F));
        await Frames(2);
        var onBox = Entity(host, "obj:doorstop");
        var at = Vec(onBox["position_m"]!);
        Check(host.HeldBy(CommandHost.PlayerAvatar) == null && onBox["held_by"] == null && Mathf.Abs(at.Y - 0.30f) < 0.0005f && at.X > 0.925f && at.X < 1.275f && at.Z > 0.025f && at.Z < 0.375f,
            $"F facing the box sets the doorstop on its top: {onBox["position_m"]!.ToJsonString()} ({notice.Text})");
        Check(host.Revision == revision + 1 && host.EntityRevision("obj:doorstop") == 1 && notice.Text == "Set Doorstop on Cardboard box.",
            "through a command with a durable receipt: the room's revision and the doorstop's moved by one");
        var saved = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(CommandHost.SavePathFor(_world.Room)));
        Check(saved.Contains("\"object_poses\"", StringComparison.Ordinal) && saved.Contains("\"obj:doorstop\"", StringComparison.Ordinal) && saved.Contains("entity.release", StringComparison.Ordinal),
            "the save holds its new place and the receipt");
        _hud._UnhandledInput(Press(Key.V));
        await Frames(2);
        _hud._UnhandledInput(Press(Key.V));
        await Frames(2);
        var pushed = Vec(Entity(host, "obj:doorstop")["position_m"]!);
        Check(Mathf.Abs(pushed.X - at.X - 0.2f) < 0.002f && Mathf.Abs(pushed.Y - 0.30f) < 0.0005f && Mathf.Abs(pushed.Z - at.Z) < 0.002f && notice.Text == "Pushed Doorstop.",
            $"V twice pushes it 20 cm along the box's top ({at.X:0.000} to {pushed.X:0.000} m)");
        await Frames(40);
        var heavyTag = _hud.FocusTag.Text;
        _hud._UnhandledInput(Press(Key.F));
        await Frames(2);
        Check(host.HeldBy(CommandHost.PlayerAvatar) == null && notice.Text.Contains("too heavy", StringComparison.Ordinal), "F on the big box says it is too heavy: " + notice.Text);
        Check(heavyTag.Contains("too heavy", StringComparison.Ordinal), $"and its tag said so before the key: \"{heavyTag}\"");
        // On the way home (B), as while climbing or diving, nothing is lit and no tag shows.
        Check(_hud.FocusTarget != null && player.RequestHome(), "still facing the box, B starts the way home");
        await Frames(3);
        var litOnTheWay = _hud.FocusTarget != null || _hud.FocusTag.Visible || (_hud.Look != null && _hud.Look.Highlight.Target != null);
        await Frames(80);
        Check(!litOnTheWay, "during the fade home nothing is lit and the tag is gone");
    }

    // ---- The Glow round: the Gubble's keys, the smart ask, the wheel, the bubble, the aura and the dusk moment ----

    private static InputEventMouseButton RightButton(bool pressed) => new() { ButtonIndex = MouseButton.Right, Pressed = pressed };

    private static InputEventMouseMotion Motion(Vector2 relative) => new() { Relative = relative };

    /// <summary>A tap of the ask button: down and up at once, the pointer still.</summary>
    private void Tap()
    {
        _hud._UnhandledInput(RightButton(true));
        _hud._UnhandledInput(RightButton(false));
    }

    /// <summary>A ray straight down onto a spot, from 30 cm above it.</summary>
    private static (Vector3, Vector3) Down(Vector3 spot) => (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.3f);

    /// <summary>A ray that meets nothing: 1 cm of air in front of the player's eye.</summary>
    private (Vector3, Vector3) Air()
    {
        var eye = _world.Player.EyeCamera.GlobalPosition;
        return (eye, eye + Vector3.Up * 0.01f);
    }

    /// <summary>The ray from the player's eye through the Gubble's middle.</summary>
    private (Vector3, Vector3) AtGubble()
    {
        var companion = _world.Companion;
        var middle = companion.GlobalPosition + Vector3.Up * (companion.BodyHeightM * 0.5f);
        var eye = _world.Player.EyeCamera.GlobalPosition;
        return (eye, eye + (middle - eye).Normalized() * 3f);
    }

    /// <summary>
    /// A spot near the Gubble, at least metres from it, that a tap would not fetch: the floor, or a thing on it that is not
    /// carryable (the Gubble waits on the rug, which is). Nearest first, eight ways round, within Glow's 2 m.
    /// </summary>
    private Vector3 FloorNearGubble(float metres)
    {
        var host = CommandHost.Of(_world)!;
        var at = _world.Companion.GlobalPosition;
        var candidates = new[] { 0f, 0.15f, 0.35f, 0.6f, 0.9f, 1.2f }.SelectMany(extra => Enumerable.Range(0, 8).Select(i =>
            at + new Vector3(Mathf.Sin(i * Mathf.Pi / 4), 0, Mathf.Cos(i * Mathf.Pi / 4)) * (metres + extra)));
        foreach (var spot in candidates)
        {
            _hud.AimRayForTests = (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.5f);
            var aim = _hud.AimNow();
            // The floor, or a thing lying on it that is not carryable (the rug the Gubble waits on).
            if (aim.Hit && !aim.AtGubble && _hud.DecideAsk(aim).Rule is not (GubbleMagic.AskRule.Fetch or GubbleMagic.AskRule.Toggle) &&
                host.Room.Bounds.HasPoint(aim.Point + Vector3.Up * 0.001f)) return aim.Point;
        }
        GD.Print("PLAY_HUD_INFO: no bare spot found near the Gubble at " + at);
        return at;
    }

    /// <summary>The Gubble beside the player, waiting, nothing running, by day, aimed at nothing.</summary>
    private async Task TestGubbleReady()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        host.PlayerGoal("follow");
        for (var i = 0; i < 600 && companion.GlobalPosition.DistanceTo(_world.Player.GlobalPosition) > 0.4f; i++) await Frames(1);
        host.PlayerGoal("stop");
        host.PlayerGoal("stay");
        await Frames(10);
        Check(companion.GlobalPosition.DistanceTo(_world.Player.GlobalPosition) <= 0.4f && host.ActiveEffectIds.Count == 0,
            $"(the Gubble waits beside the player, {companion.GlobalPosition.DistanceTo(_world.Player.GlobalPosition):0.00} m away, no glows running)");
    }

    /// <summary>The profile's Gubble colour is its aura: the glow will take it (CompanionAvatar.SetAuraColor).</summary>
    private void TestAuraFromProfile()
    {
        var companion = _world.Companion;
        Check(companion.AppearanceColor.ToHtml(false) == "a18cc3" && companion.AuraColor is { } aura && aura.IsEqualApprox(companion.AppearanceColor) &&
              companion.GetMeta(LookDirector.AuraColorMeta).AsColor().IsEqualApprox(aura),
            $"the profile's companion_color ({ProfileGubbleColour}) is the Gubble's body and its aura, carried in the meta the look reads ({companion.AuraColor?.ToHtml(false)})");
    }

    /// <summary>The dusk moment's clock, alone: by day nothing; dark, it shows; ignored 20 s, once more; then it waits for the next dusk; a cast ends it.</summary>
    private void TestDuskLogic()
    {
        float? level = 0.9f;
        var dusk = new DuskMoment();
        int shows = 0, hides = 0;
        void Run(double seconds, bool used = false)
        {
            for (var t = 0; t < (int)Math.Round(seconds * 60); t++)
            {
                var step = dusk.Update(1.0 / 60, () => level, LookDirector.DarkThreshold, used);
                if (step == DuskMoment.Step.Show) shows++;
                if (step == DuskMoment.Step.Hide) hides++;
            }
        }
        Run(60);
        Check(shows == 0 && dusk.Checks is >= 239 and <= 241, $"dusk moment: by day it never shows, and the light is read four times a second ({dusk.Checks} readings in 60 s)");
        level = 0.3f;
        Run(1);
        Check(shows == 1 && dusk.Showing, "the first time it is dark, the hint shows");
        Run(DuskMoment.ShowsForS);
        Check(hides == 1 && !dusk.Showing && shows == 1, $"it goes after {DuskMoment.ShowsForS} s, and does not come straight back");
        Run(DuskMoment.RepeatAfterS - DuskMoment.ShowsForS);
        Check(shows == 2 && dusk.Showing, "ignored for 20 s, it shows once more");
        Run(60);
        Check(shows == 2 && !dusk.Showing, "then it waits: no third time in the same dark");
        level = 0.9f;
        Run(1);
        level = 0.3f;
        Run(1);
        Check(shows == 3 && dusk.Showing, "light, then dark again: the next dusk shows it again");
        Run(1, used: true);
        Check(!dusk.Showing && hides == 3, "casting Glow hides it at once");
        for (var i = 0; i < 4; i++)
        {
            level = i % 2 == 0 ? 0.9f : 0.3f;
            Run(30, used: true);
        }
        Check(shows == 3, "and after a cast it never shows again, through days and nights");
        level = null;
        var quiet = new DuskMoment();
        for (var t = 0; t < 600; t++) quiet.Update(1.0 / 60, () => level, LookDirector.DarkThreshold, false);
        Check(!quiet.Showing && quiet.TotalShown == 0, "without a light reading (no look) it never shows");
    }

    /// <summary>The dusk moment in the room: the hint beside the Gubble with the real keys, the shiver and the dim, and 1 ends it for good.</summary>
    private async Task TestDuskInRoom()
    {
        var companion = _world.Companion;
        _hud.ResetDuskForTests();
        _hud.LightForTests = _ => 0.9f;
        await Frames(30);
        Check(!_hud.DuskHintLabel.Visible && _hud.Dusk.TotalShown == 0 && !_hud.HasUsedGlow, "by day no hint shows, and this player has never cast Glow");
        Check(_hud.Look != null && _hud.LightAt(_world.Player.GlobalPosition) == 0.9f && (_hud.LightForTests = null) == null &&
              _hud.LightAt(_world.Player.GlobalPosition) == _hud.Look.LightLevelAt(_world.Player.GlobalPosition), "(the HUD reads the look's LightLevelAt, unless a test says otherwise)");
        _hud.LightForTests = _ => 0.2f;
        await Frames(20);
        var camera = _hud.GetViewport().GetCamera3D();
        var beside = camera.UnprojectPosition(companion.GlobalPosition);
        Check(_hud.DuskHintLabel.Visible && _hud.DuskHintLabel.Text == "1 or right-click: Light!" && (_hud.DuskHintLabel.Position - beside).Length() < 160,
            $"dark at the player: one hint beside the Gubble, with the real keys: \"{_hud.DuskHintLabel.Text}\" at {_hud.DuskHintLabel.Position} (the Gubble at {beside})");
        Check(_hud.Cue.Dimmed && _hud.Cue.LastGesture == CompanionAvatar.Gesture.Shiver && _hud.Cue.Icon == GubbleMagic.SlotIcons[0],
            "the Gubble shivers and dims, a sun in its thought bubble");
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Key1));
        await Frames(2);
        Check(_hud.HasUsedGlow && !_hud.DuskHintLabel.Visible && !_hud.Cue.Dimmed, "1 casts Glow: the hint goes and the Gubble brightens");
        Check(ProfileValue("has_used_glow").VariantType == Variant.Type.Bool && ProfileValue("has_used_glow").AsBool() && ProfileValue("companion_color").AsInt32() == ProfileGubbleColour,
            "the profile keeps has_used_glow, and the rest of it as it was saved");
        _hud.LightForTests = _ => 0.9f;
        await Frames(30);
        _hud.LightForTests = _ => 0.2f;
        await Frames(60);
        Check(!_hud.DuskHintLabel.Visible && _hud.Dusk.TotalShown == 1, "and it never shows again, the next dusk included");
        _hud._UnhandledInput(Press(Key.X));
        _hud.LightForTests = _ => 0.9f;
    }

    /// <summary>The new default keys: 1 Glow on the Gubble by day, X stop (ending the glows), 2 to 5 shrug, Q come then follow.</summary>
    private async Task TestGubbleKeys()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        var look = _world.Look;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Key1));
        var ids = host.ActiveEffectIds;
        Check(ids.Count == 1 && look.GlowRoot(ids[0]) is { } halo && halo.GetNodeOrNull("Core") == null && look.GlowCount == 1 &&
              companion.LastGesture == CompanionAvatar.Gesture.CastSelf && companion.LastCastToward == null,
            "1 aimed at nothing casts Glow on the Gubble itself (a halo that follows it), and the Gubble lifts as it lights");
        _hud._UnhandledInput(Press(Key.X));
        Check(host.ActiveEffectIds.Count == 0 && look.GlowCount == 0 && companion.CurrentIntent == "stop", "X stops the Gubble and ends its glows (goal.stop)");
        foreach (var key in new[] { Key.Key5 })
        {
            var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
            _hud._UnhandledInput(Press(key));
            Check(_hud.Cue.State == "shrug" && _hud.Cue.Reason == GubbleMagic.NotYet && _hud.Cue.LastGesture == CompanionAvatar.Gesture.Shrug &&
                  host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && host.ActiveEffectIds.Count == 0,
                $"{OS.GetKeycodeString(key)}: a slot this island has no ability for shrugs \"not yet\" and sends nothing");
        }
        // 3 and 4: Bubbles and Fireworks, through Glow's cast path. Since the Bubble's redesign (decisions 55 to 60, Kernel/Bubble) the
        // Bubble is one bubble at a spot, the place deciding, and none on the Gubble itself: aimed at nothing the host refuses it (the
        // HUD's right-click flow is the Sonnet pass's); aimed at the floor it is the one bubble there, no stream the look blows.
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Key3));
        Check(host.ActiveBubbles.Count == 0 && host.ActiveEffectIds.Count == 0 && _hud.Cue.State == "refused",
            "3 aimed at nothing: no Bubble on the Gubble itself any more; the host refuses it and the Gubble shakes its head: " + _hud.Cue.Reason);
        var spot = FloorNearGubble(0.4f);
        _hud.AimRayForTests = Down(spot);
        _hud._UnhandledInput(Press(Key.Key3));
        var bubbles = host.ActiveBubble?.Id;
        Check(bubbles != null && host.ActiveEffectIds.Count == 0 && look.KindOf(bubbles) == LookDirector.EffectKind.None && look.BubbleRoot(bubbles) is { } && _hud.Cue.Icon == GubbleMagic.SlotIcons[2] &&
              _hud.Cue.State == "done" && companion.LastCastCategory == "float",
            "3 aimed at the floor: the one Bubble there, no stream of small bubbles: " + host.ActiveBubble);
        _hud._UnhandledInput(Press(Key.Key4));
        var fireworks = host.ActiveEffectIds.LastOrDefault();
        Check(fireworks != null && fireworks != bubbles && look.KindOf(fireworks) == LookDirector.EffectKind.Fireworks && _hud.Cue.Icon == GubbleMagic.SlotIcons[3] && _hud.Cue.State == "done" &&
              companion.LastGesture == CompanionAvatar.Gesture.Cast && companion.LastCastToward is { } toward && toward.DistanceTo(spot) < 0.001f && companion.LastCastCategory == "burst",
            "4 aimed at the floor: Fireworks at the spot, the Gubble tossing the rocket toward it (a quicker gesture)");
        Check(CompanionAvatar.CastSeconds("float") == CompanionAvatar.CastSeconds("light") && CompanionAvatar.CastSeconds("burst") < CompanionAvatar.CastSeconds("light"),
            "the Bubble is cast like Glow, one bubble at a spot, with no longer blowing gesture, and Fireworks toss quicker");
        _hud._UnhandledInput(Press(Key.X));
        Check(host.ActiveEffectIds.Count == 0 && host.ActiveBubbles.Count == 0 && look.EffectCount == 0, "X pops the bubble and ends the fireworks with the Gubble's goal");
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Q));
        Check(companion.CurrentIntent == "come", "Q calls the Gubble (come)");
        var followed = false;
        for (var i = 0; i < 600 && !followed; i++)
        {
            await Frames(1);
            followed = companion.CurrentIntent == "follow";
        }
        Check(followed, "and once beside the player it follows: the recall");
        _hud._UnhandledInput(Press(Key.X));
        _hud._UnhandledInput(Press(Key.Q));
        _hud._UnhandledInput(Press(Key.X));
        await Frames(60);
        Check(companion.CurrentIntent == "stop", "a newer order (X) cancels the recall's follow");
    }

    /// <summary>Each smart-ask rule, the first match winning, the reticle's tag before the press, and the target frozen at the press.</summary>
    private async Task TestSmartAsk()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.LightForTests = _ => 0.9f;
        host.PlayerGoal("stay");
        // 1. Aimed at the Gubble: switch stay and follow.
        _hud.AimRayForTests = AtGubble();
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.Toggle && _hud.AskTag.Visible && _hud.AskTag.Text == "right-click: → follow me",
            $"aimed at the Gubble while it waits, the reticle's tag says a tap will ask it to follow: \"{_hud.AskTag.Text}\"");
        Tap();
        Check(companion.CurrentIntent == "follow", "a tap on the Gubble: it follows");
        _hud.AimRayForTests = AtGubble();
        Tap();
        Check(companion.CurrentIntent == "stay", "another tap on it: it waits");
        // 2. Aimed at a carryable thing: fetch it, the target frozen at the press.
        var door = SandboxControls.Box(Entity(host, "obj:doorstop"));
        _hud.AimRayForTests = Down(new Vector3(door.GetCenter().X, door.End.Y, door.GetCenter().Z));
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.Fetch && _hud.AskPreview.Target == "obj:doorstop" && _hud.AskTag.Text == "right-click: ✋ fetch it",
            $"aimed at the doorstop, a tap will fetch it; the tag names no thing: \"{_hud.AskTag.Text}\"");
        _hud.LightForTests = _ => 0.2f;
        Check(_hud.DecideAsk(_hud.AimNow()).Rule == GubbleMagic.AskRule.Fetch, "a carryable thing in the dark is still fetched: the first rule that matches wins");
        _hud.LightForTests = _ => 0.9f;
        _hud._UnhandledInput(RightButton(true));
        var floor = FloorNearGubble(0.3f);
        _hud.AimRayForTests = Down(floor);
        _hud._UnhandledInput(RightButton(false));
        Check(host.RunningGoal(CommandHost.CompanionAvatarId) is { } fetch && fetch.Goal == "fetch" && fetch.Target == "obj:doorstop",
            "the target froze at the press: the aim moved to the floor before the release, and the Gubble still goes to fetch the doorstop");
        await Frames(3);
        Check(_hud.Cue.State == "thinking" && _hud.Cue.Icon == GubbleMagic.FetchIcon, "and its bubble waits for the job: no \"done\" on the receipt alone");
        _hud._UnhandledInput(Press(Key.X));
        await Frames(2);
        Check(host.RunningGoal(CommandHost.CompanionAvatarId) == null && host.HeldBy(CommandHost.CompanionAvatarId) == null, "(X calls the fetch off)");
        // 3. A dark spot within Glow's reach: glow there (a wisp at the spot).
        _hud.LightForTests = _ => 0.2f;
        floor = FloorNearGubble(0.3f);
        _hud.AimRayForTests = Down(floor);
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.Glow && _hud.AskTag.Text == "right-click: ☀ glow there" + PriceWords("glow"),
            $"aimed at a dark spot near the Gubble, a tap will light it, and the reticle says what that costs: \"{_hud.AskTag.Text}\"");
        Tap();
        var ids = host.ActiveEffectIds;
        Check(ids.Count == 1 && companion.LastGesture == CompanionAvatar.Gesture.Cast && companion.LastCastToward is { } toward && toward.DistanceTo(floor) < 0.001f && _hud.Cue.State == "done",
            $"a tap lights it: a light at the spot, the Gubble leaning toward it ({_hud.Cue.State}: {_hud.Cue.Reason})");
        var far = _world.Companion.GlobalPosition + Vector3.Right * 3f;
        Check(_hud.DecideAsk(new GubbleMagic.Aim(true, far, null, false)).Rule == GubbleMagic.AskRule.Look, "a dark spot beyond Glow's reach (2 m): go and look instead");
        _hud._UnhandledInput(Press(Key.X));
        // 4. Otherwise: go and look there (the old point order, aimed).
        _hud.LightForTests = _ => 0.9f;
        floor = FloorNearGubble(0.35f);
        _hud.AimRayForTests = Down(floor);
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.Look && _hud.AskTag.Text == "right-click: ◎ go and look", $"by day, aimed at the floor: go and look: \"{_hud.AskTag.Text}\"");
        Tap();
        Check(companion.CurrentIntent == "go_to", "a tap sends the Gubble there");
        var pointed = false;
        for (var i = 0; i < 600 && !pointed; i++)
        {
            await Frames(1);
            pointed = companion.CurrentIntent == "point";
        }
        Check(pointed && companion.IsPointing && companion.LookTarget.DistanceTo(floor) < 0.01f && _hud.Cue.State == "done", "and there it looks and points at the spot (point is reachable)");
        // Nothing hit: nothing to ask.
        _hud.AimRayForTests = Air();
        await Frames(10);
        var intent = companion.CurrentIntent;
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.None && !_hud.AskTag.Visible, "aimed at nothing, the reticle offers nothing");
        Tap();
        Check(companion.CurrentIntent == intent && _hud.Cue.State == "shrug", "and a tap there only shrugs");
        _hud._UnhandledInput(Press(Key.X));
    }

    /// <summary>The wheel: a flick picks before it is drawn, a hold opens it and pauses, the centre cancels, a wedge is chosen on release, "?" shrugs.</summary>
    private async Task TestWheel()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.LightForTests = _ => 0.9f;
        var wheelSpot = FloorNearGubble(0.3f);
        _hud.AimRayForTests = Down(wheelSpot);
        host.PlayerGoal("follow");
        // Nine wedges since Fire's (decision 77): Stay sits at 120 degrees, a flick to the lower right.
        _hud._UnhandledInput(RightButton(true));
        _hud._UnhandledInput(Motion(new Vector2(52, 30)));
        var drawn = _hud.Wheel.Open;
        _hud._UnhandledInput(RightButton(false));
        Check(!drawn && companion.CurrentIntent == "stay" && !GetTree().Paused, "a flick to the lower right and release picks Stay before the wheel is even drawn");
        _hud._UnhandledInput(RightButton(true));
        await Frames(4);
        Check(!_hud.Wheel.Open && !GetTree().Paused, "a short hold has not opened it yet");
        await Frames(20);
        Check(_hud.Wheel.Open && _hud.Wheel.Visible && GetTree().Paused, "held, the wheel opens and the game pauses");
        var labels = _hud.Wheel.WedgeLabels.Select(l => l.Text).ToArray();
        Check(labels[0] == "☀\n1 Glow" && labels[3] == "‖\nStay" && labels[6] == "✋\nFetch" && labels[7] == "↩\nCome (Q)",
            "Glow is up (the pack's light ability, its display_name), then clockwise Stay, Fetch lower left, Come left: " + string.Join(" | ", labels).Replace("\n", " "));
        Check(labels[1] == "✸\n4 Fireworks" && labels[8] == "○\n3 Bubbles" && _hud.Wheel.WedgeLabels[1].Modulate.A == 1f && _hud.Wheel.WedgeLabels[8].Modulate.A == 1f,
            "Fireworks (upper right) and Bubbles (upper left) are the island's now, bright in their fixed places: " + labels[1].Replace("\n", " ") + " | " + labels[8].Replace("\n", " "));
        Check(labels[2].StartsWith("♨\n6 Fire", StringComparison.Ordinal) && labels[4] == "✿\n2 Grow" && _hud.Wheel.WedgeLabels[4].Modulate.A == 1f,
            "Fire is the ninth wedge just after Fireworks (decision 77), and Grow is in its fixed growth wedge: " + labels[2].Replace("\n", " "));
        Check(new[] { 5 }.All(i => labels[i] == $"{GubbleMagic.Wedges[i].Icon}\n{GubbleMagic.Wedges[i].Slot + 1} {GubbleMagic.Wedges[i].Name}\n{GubbleWheel.LackingWords}" &&
              _hud.Wheel.WedgeLabels[i].Modulate.A < 0.5f && !labels[i].Contains(GubbleMagic.UnknownIcon, StringComparison.Ordinal)),
            "Build, which this island lacks, stays dim in its fixed place with its name and \"not yet\": " + labels[5].Replace("\n", " "));
        Check(_hud.Wheel.Title.Text == "The Gubble's magic" && _hud.Wheel.Title.IsVisibleInTree() && companion.AuraColor is { } aura && _hud.Wheel.Rim.IsEqualApprox(new Color(aura, 1)),
            $"the wheel says whose it is: \"{_hud.Wheel.Title.Text}\" over it, its rim the Gubble's aura colour");
        var intent = companion.CurrentIntent;
        var effects = host.ActiveEffectIds.Count;
        _hud._UnhandledInput(Motion(new Vector2(6, -4)));
        _hud._UnhandledInput(RightButton(false));
        Check(!_hud.Wheel.Open && !GetTree().Paused && companion.CurrentIntent == intent && host.ActiveEffectIds.Count == effects,
            "let go in the centre: it cancels, nothing is asked, the game resumes");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        _hud._UnhandledInput(Motion(new Vector2(3, -90)));
        Check(_hud.Wheel.Hovered == 0, "moving up hovers Glow");
        _hud._UnhandledInput(RightButton(false));
        Check(host.ActiveEffectIds.Count == effects + 1 && !GetTree().Paused && companion.LastCastToward is { } spot && spot.DistanceTo(wheelSpot) < 0.001f &&
              companion.LastGesture == CompanionAvatar.Gesture.Cast,
            "held, moved to Glow and let go: the Gubble lights the spot the press froze, dark or not (by day, the floor beside it)");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        _hud._UnhandledInput(Motion(new Vector2(-31, 85)));
        Check(_hud.Wheel.Hovered == 5, "down and a little left hovers Build (200 degrees, nine wedges)");
        _hud._UnhandledInput(RightButton(false));
        Check(_hud.Cue.State == "shrug" && _hud.Cue.Reason == GubbleMagic.NotYet && host.ActiveEffectIds.Count == effects + 1, "the unavailable Build wedge shrugs \"not yet\" and casts nothing");
        _hud._UnhandledInput(RightButton(true));
        _hud._UnhandledInput(Motion(new Vector2(-60, 0)));
        _hud._UnhandledInput(RightButton(false));
        Check(companion.CurrentIntent is "come" or "follow", "Come (left) is the recall: " + companion.CurrentIntent);
        _hud._UnhandledInput(RightButton(true));
        _hud._UnhandledInput(Motion(new Vector2(-50, 50)));
        _hud._UnhandledInput(RightButton(false));
        Check(_hud.Cue.State == "shrug" && _hud.Cue.Reason == GubbleMagic.NothingToFetch, "Fetch (lower left) aimed at the floor: nothing to fetch, nothing sent");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        intent = companion.CurrentIntent;
        _hud._UnhandledInput(Press(Key.C));
        Check(_hud.Wheel.Open && GetTree().Paused && !_hud.SettingsOpen, "while the wheel is open the other keys wait (C opens no panel)");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(!_hud.Wheel.Held && !_hud.Wheel.Open && !GetTree().Paused && companion.CurrentIntent == intent, "Esc cancels the wheel and resumes the game");
        _hud._UnhandledInput(RightButton(false));
        Check(companion.CurrentIntent == intent && !GetTree().Paused, "and the button's release after it asks nothing");
        _hud._UnhandledInput(Press(Key.X));
    }

    /// <summary>
    /// How far sideways of the Gubble's feet the bubble's middle may be: it hangs on the Gubble's head and is measured from its feet, so
    /// the camera's tilt and the Gubble's hover put the two a few tens of pixels apart (39.9 of the old 40 px before the pulse's tests
    /// moved the suite's frame count by a few hundred; the bubble does not move for a pulse or for the pips).
    /// </summary>
    private const float CueSidewaysPx = 56f;

    /// <summary>The bubble: the icon at once, "done" on the host's result, a refusal as a head-shake with the host's reason (never a name).</summary>
    private async Task TestAcknowledgement()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Key1));
        Check(_hud.Cue.Icon == GubbleMagic.SlotIcons[0] && _hud.Cue.State == "done" && _hud.Cue.Bubble.Visible && _hud.Cue.Bubble.Text == "( ☀ ✓ )",
            $"at once, the Gubble's thought bubble shows the ability's icon, and the host's receipt makes it done: \"{_hud.Cue.Bubble.Text}\"");
        var camera = _hud.GetViewport().GetCamera3D();
        var over = camera.UnprojectPosition(companion.GlobalPosition);
        var bubbleRect = new Rect2(_hud.Cue.Bubble.Position, _hud.Cue.Bubble.Size);
        Check(_hud.Cue.Bubble.GetThemeFontSize("font_size") >= 18 && _hud.Cue.Bubble.GetThemeConstant("outline_size") >= 6 && _hud.Cue.Bubble.IsVisibleInTree() &&
              Mathf.Abs(bubbleRect.GetCenter().X - over.X) < CueSidewaysPx && bubbleRect.End.Y <= over.Y && over.Y - bubbleRect.End.Y < 260,
            $"screen text, at least as large as the HUD's own (18 px) and outlined dark, centred just over the Gubble ({bubbleRect} over {over})");
        Check(_hud.Cue.LastGesture == CompanionAvatar.Gesture.CastSelf, "and the Gubble lifts as its own glow comes up (its cast gesture, not the wiggle)");
        const int max = 3;
        Check(host.Rules!.Ability("glow")!.MaxActive == 5 && host.EffectiveEffectLimit("glow") == max,
            "the new rules permit five earned lights, while this Lantern-only profile still holds three");
        for (var i = 0; i < max * 2 && host.ActiveEffectIds.Count < max; i++) _hud._UnhandledInput(Press(Key.Key1));
        companion.SetDisplayName("Pip the Brave");
        _hud._UnhandledInput(Press(Key.Key1));
        const string reason = "As many of these are running as the island allows";
        Check(_hud.Cue.State == "refused" && _hud.Cue.Reason == reason && _hud.Cue.ReasonLabel.Visible && _hud.Cue.ReasonLabel.Text == reason &&
              _hud.Cue.LastGesture == CompanionAvatar.Gesture.Shake && _hud.Cue.Bubble.Text == "( ☀ ✕ )",
            $"one glow more than the island allows: a head-shake and the host's reason, short, beside the bubble: \"{_hud.Cue.Reason}\"");
        Check(!_hud.Cue.Reason.Contains("Pip", StringComparison.Ordinal) && host.ActiveEffectIds.Count == max, "the reason is the host's, never a name (the Gubble renamed Pip says the same), and nothing more was lit");
        companion.SetDisplayName(CompanionAvatar.DefaultName);
        Check(GubbleMagic.ShortReason("That spot is beyond the Gubble's reach; it must come closer first.") == "That spot is beyond the Gubble's reach" &&
              GubbleMagic.ShortReason(new string('a', 90)).Length == 60 && GubbleMagic.ShortReason(null) == "can't", "a reason is cut to its first clause, at most 60 characters");
        await Frames((int)(GubbleCue.ShowS * 60) + 20);
        Check(!_hud.Cue.Bubble.Visible && !_hud.Cue.ReasonLabel.Visible && _hud.Cue.State == "", $"after {GubbleCue.ShowS} s the bubble clears");
        _hud._UnhandledInput(Press(Key.X));
    }

    // ---- The Glow playtest (9 October): readable words, the free cursor and its look drag, Glow where you point ----

    /// <summary>
    /// The words on the Gubble read at a glance: the bubble and its reason are screen text on the HUD, at least as large as the
    /// HUD's own (18 px) and outlined dark, the bubble just over the Gubble and the reason over the bubble, in the views that
    /// frame it; the AI link's state line is as large as the name tag and outlined.
    /// </summary>
    private async Task TestReadableWords()
    {
        var companion = _world.Companion;
        Check(_hud.Cue.GetParent() == _hud && _hud.Cue.Bubble.GetParent() == _hud.Cue &&
              _hud.Cue.Bubble.GetThemeFontSize("font_size") >= 18 && _hud.Cue.ReasonLabel.GetThemeFontSize("font_size") >= 18 &&
              _hud.Cue.Bubble.GetThemeConstant("outline_size") >= 6 && _hud.Cue.ReasonLabel.GetThemeConstant("outline_size") >= 6,
            $"the bubble ({_hud.Cue.Bubble.GetThemeFontSize("font_size")} px) and its reason ({_hud.Cue.ReasonLabel.GetThemeFontSize("font_size")} px) are the HUD's screen text, as large as its own or larger, outlined dark");
        foreach (var mode in new[] { 1, 2, 3 })
        {
            _hud.SetViewMode(mode);
            _hud.Cue.Refuse("That spot is beyond the Gubble's reach; it must come closer first.");
            await Frames(3);
            var camera = _hud.GetViewport().GetCamera3D();
            var feet = camera.UnprojectPosition(companion.GlobalPosition);
            var framed = !camera.IsPositionBehind(companion.GlobalPosition) && _hud.GetViewport().GetVisibleRect().Grow(-120).HasPoint(feet);
            var bubble = new Rect2(_hud.Cue.Bubble.Position, _hud.Cue.Bubble.Size);
            var reason = new Rect2(_hud.Cue.ReasonLabel.Position, _hud.Cue.ReasonLabel.Size);
            if (!framed) GD.Print($"PLAY_HUD_INFO: F{mode + 1} does not frame the Gubble here ({feet})");
            Check(!framed || (_hud.Cue.Bubble.Visible && _hud.Cue.ReasonLabel.Visible && Mathf.Abs(bubble.GetCenter().X - feet.X) < CueSidewaysPx &&
                  bubble.End.Y < feet.Y && feet.Y - bubble.End.Y < 260f && reason.End.Y <= bubble.Position.Y + 1f),
                $"F{mode + 1}: the bubble sits just over the Gubble ({bubble} over its feet at {feet}) and the reason over the bubble ({reason})");
        }
        _hud.Cue.Clear();
        _hud.SetViewMode(1);
        await Frames(2);
        Check(!_hud.Cue.Bubble.Visible && !_hud.Cue.ReasonLabel.Visible, "cleared, neither shows");
        if (GarageGame.Native.Companion.CompanionBridge.Of(_world)?.Cue is { } status)
            Check(status.FontSize >= 30 && status.OutlineSize >= 8, $"the AI link's state line is as large as the name tag ({status.FontSize}) with a thick outline ({status.OutlineSize})");
        Check(companion.GetNode<Label3D>("CompanionLabel").OutlineSize >= 8, "and so is the name tag's outline");
    }

    private static InputEventMouseButton LeftButton(bool pressed) => new() { ButtonIndex = MouseButton.Left, Pressed = pressed };

    /// <summary>
    /// The cursor is free and a left drag looks around: nothing captures the mouse; a click without a drag does nothing; past
    /// LookDrag.StartPx the drag turns the body and eye in F1 and F2 and orbits in F3 as the captured mouse did, and nothing in
    /// F4; the aim stays where the cursor was pressed; a release the window never sees, Esc, the panel and the wheel end it.
    /// </summary>
    private async Task TestLookDrag()
    {
        var player = _world.Player;
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.AimRayForTests = Air();
        _hud.DragHeldForTests = () => true;
        try
        {
            _hud.SetViewMode(1);
            await Frames(2);
            Check(Input.MouseMode != Input.MouseModeEnum.Captured && !_hud.Drag.Pressed, "nothing captures the mouse: the cursor is free");
            var yaw = player.GlobalRotation.Y;
            var pitch = player.EyeCamera.Rotation.X;
            var intent = companion.CurrentIntent;
            var effects = host.ActiveEffectIds.Count;
            var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(LookDrag.StartPx * 0.6f, 0)));
            var armed = _hud.Drag.Pressed && !_hud.Drag.Dragging;
            _hud._UnhandledInput(LeftButton(false));
            Check(armed && !_hud.Drag.Pressed && Mathf.IsEqualApprox(yaw, player.GlobalRotation.Y) && Mathf.IsEqualApprox(pitch, player.EyeCamera.Rotation.X) &&
                  companion.CurrentIntent == intent && host.ActiveEffectIds.Count == effects && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts,
                $"a left click that moves less than {LookDrag.StartPx} px does nothing: no turn, no order, no command");

            _hud._UnhandledInput(LeftButton(true));
            var anchor = _hud.Drag.Anchor;
            _hud._UnhandledInput(Motion(new Vector2(3, 0)));
            var early = _hud.Drag.Dragging || !Mathf.IsEqualApprox(yaw, player.GlobalRotation.Y);
            _hud._UnhandledInput(Motion(new Vector2(40, -20)));
            var turned = Mathf.AngleDifference(yaw, player.GlobalRotation.Y);
            Check(!early && _hud.Drag.Dragging && _hud.AimScreenPoint() == anchor, "past the threshold it is a drag, and the aim stays where the cursor was pressed while it is hidden");
            Check(Mathf.Abs(turned + 40 * SmallPlayerController.LookRadiansPerPixel) < 1e-4f && Mathf.Abs(player.EyeCamera.Rotation.X - (pitch + 20 * SmallPlayerController.LookRadiansPerPixel)) < 1e-4f &&
                  Mathf.IsEqualApprox(SmallPlayerController.LookRadiansPerPixel, RoomHud.MouseRadiansPerPixel),
                $"in F2 the drag turns the body and tilts the eye as the captured mouse did, {SmallPlayerController.LookRadiansPerPixel} rad a pixel ({turned:0.0000} rad)");
            _hud._UnhandledInput(LeftButton(false));
            Check(!_hud.Drag.Pressed && !_hud.Drag.Dragging, "letting go ends it (the cursor comes back where it was pressed)");

            _hud.SetViewMode(2);
            var orbit = _hud.DioramaYaw;
            var bodyYaw = player.GlobalRotation.Y;
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(60, 0)));
            _hud._UnhandledInput(LeftButton(false));
            Check(Mathf.Abs(Mathf.AngleDifference(orbit, _hud.DioramaYaw) + 60 * RoomHud.MouseRadiansPerPixel) < 1e-4f && Mathf.IsEqualApprox(bodyYaw, player.GlobalRotation.Y),
                "in F3 the drag orbits the camera round the player, as the captured mouse did; the body does not turn");

            _hud.SetViewMode(3);
            var iso = _hud.IsoYaw;
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(60, 30)));
            var isoArmed = _hud.Drag.Pressed;
            _hud._UnhandledInput(LeftButton(false));
            Check(!isoArmed && Mathf.IsEqualApprox(iso, _hud.IsoYaw) && Mathf.IsEqualApprox(bodyYaw, player.GlobalRotation.Y), "in F4 a drag turns nothing ([ and ] turn it)");
            _hud.SetViewMode(1);

            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(30, 0)));
            var dragging = _hud.Drag.Dragging;
            _hud.DragHeldForTests = () => false;
            await Frames(2);
            Check(dragging && !_hud.Drag.Pressed, "a release outside the window (the button found let go) never leaves a drag stuck");
            _hud.DragHeldForTests = () => true;

            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(30, 0)));
            _hud._UnhandledInput(Press(Key.Escape));
            Check(!_hud.Drag.Pressed, "Esc calls a drag off");
            _hud._UnhandledInput(LeftButton(false));

            _hud._UnhandledInput(Press(Key.C));
            yaw = player.GlobalRotation.Y;
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(30, 0)));
            var inPanel = _hud.Drag.Pressed || !Mathf.IsEqualApprox(yaw, player.GlobalRotation.Y);
            _hud._UnhandledInput(LeftButton(false));
            _hud._UnhandledInput(Press(Key.C));
            Check(!inPanel && !_hud.SettingsOpen, "with the appearance panel open a drag turns nothing (the panel keeps the mouse)");

            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(30, 0)));
            _hud._UnhandledInput(RightButton(true));
            var wheelOverDrag = !_hud.Drag.Pressed && _hud.Wheel.Held;
            _hud._UnhandledInput(RightButton(false));
            _hud._UnhandledInput(LeftButton(false));
            Check(wheelOverDrag && !_hud.Wheel.Held && !GetTree().Paused, "the right button during a drag ends the drag first, and the wheel works as ever");
        }
        finally
        {
            _hud.DragHeldForTests = null;
            if (_hud.SettingsOpen) _hud._UnhandledInput(Press(Key.C));
            _hud.SetViewMode(1);
            _hud._UnhandledInput(Press(Key.X));
        }
    }

    /// <summary>A bare floor spot, in the player's sight, between min and max metres from the Gubble (null when the room has none).</summary>
    private Vector3? FloorAway(float min, float max)
    {
        var host = CommandHost.Of(_world)!;
        var at = _world.Companion.GlobalPosition;
        for (var d = min; d <= max; d += 0.15f)
            for (var i = 0; i < 32; i++)
            {
                var spot = at + new Vector3(Mathf.Sin(i * Mathf.Pi / 16), 0, Mathf.Cos(i * Mathf.Pi / 16)) * d;
                if (!host.Room.Bounds.HasPoint(spot + Vector3.Up * 0.01f)) continue;
                _hud.AimRayForTests = (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.5f);
                var aim = _hud.AimNow();
                if (!aim.Hit || aim.AtGubble || _hud.DecideAsk(aim).Rule is GubbleMagic.AskRule.Fetch or GubbleMagic.AskRule.Toggle) continue;
                var distance = at.DistanceTo(aim.Point);
                if (distance < min || distance > max || host.EffectSpot("glow", aim.Point) is not { InSight: true }) continue;
                return aim.Point;
            }
        return null;
    }

    /// <summary>
    /// Glow goes where you point (the Glow playtest): by day too; a spot beyond reach sends the Gubble closer, which stops once the
    /// spot is within reach and casts there; a newer order drops the waiting cast; the host still refuses a cast beyond reach.
    /// </summary>
    private async Task TestGlowWherePointed()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        var reach = (float)host.Rules!.Ability("glow")!.ReachM;
        _hud.SetViewMode(1);
        _hud.LightForTests = _ => 0.9f;
        host.PlayerGoal("stay");
        await Frames(5);
        var near = FloorNearGubble(0.4f);
        _hud.AimRayForTests = Down(near);
        _hud._UnhandledInput(Press(Key.Key1));
        Check(host.ActiveEffectIds.Count == 1 && companion.LastGesture == CompanionAvatar.Gesture.Cast && companion.LastCastToward is { } toward && toward.DistanceTo(near) < 0.001f && !_hud.CastWaiting,
            "by day, 1 puts the light where you point (not on the Gubble), and the Gubble leans toward it as it casts");
        _hud._UnhandledInput(Press(Key.X));

        if (FloorAway(reach + 0.35f, reach + 1.4f) is not { } far)
        {
            Check(false, "a floor spot beyond Glow's reach, in sight, to point at");
            return;
        }
        _hud.AimRayForTests = Down(far);
        Check(host.PlayerEffect("glow", far)["ok"]!.GetValue<bool>() == false && host.ActiveEffectIds.Count == 0,
            "the host itself still refuses a light beyond the Gubble's reach");
        _hud._UnhandledInput(Press(Key.Key1));
        Check(host.ActiveEffectIds.Count == 0 && _hud.CastWaiting && companion.CurrentIntent == "go_to" && _hud.Cue.State == "thinking",
            $"1 at a spot {companion.GlobalPosition.DistanceTo(far):0.00} m away (reach {reach} m): no light yet, the Gubble sets off, its bubble thinking");
        var refused = false;
        for (var i = 0; i < 1200 && host.ActiveEffectIds.Count == 0 && _hud.CastWaiting; i++)
        {
            await Frames(1);
            refused |= _hud.Cue.State == "refused";
        }
        var distance = companion.GlobalPosition.DistanceTo(far);
        Check(host.ActiveEffectIds.Count == 1 && !refused && companion.LastCastToward is { } there && there.DistanceTo(far) < 0.001f && _hud.Cue.State == "done",
            "it casts there once it is near enough, never refused on the way");
        Check(distance <= reach && distance > reach - 0.6f && companion.CurrentIntent == "stay",
            $"it stopped as soon as the spot was within its reach, {distance:0.00} m from it, and waits there (not at the spot itself)");
        _hud._UnhandledInput(Press(Key.X));

        if (FloorAway(reach + 0.35f, reach + 2.5f) is { } again)
        {
            _hud.AimRayForTests = Down(again);
            _hud._UnhandledInput(Press(Key.Key1));
            var waiting = _hud.CastWaiting;
            _hud._UnhandledInput(Press(Key.Q));
            await Frames(90);
            Check(waiting && !_hud.CastWaiting && host.ActiveEffectIds.Count == 0, "a newer order (Q) drops the waiting cast: nothing is lit");
        }
        else GD.Print("PLAY_HUD_INFO: no second far spot from here");
        _hud._UnhandledInput(Press(Key.X));
        _hud.AimRayForTests = Air();
    }


    /// <summary>The Gubble fetches the doorstop and comes back holding it (the job done); true when it holds it.</summary>
    private async Task<bool> GubbleFetchesDoorstop()
    {
        var host = CommandHost.Of(_world)!;
        var fetch = host.PlayerGoal("fetch", target: "obj:doorstop");
        if (!fetch["ok"]!.GetValue<bool>()) { GD.Print("PLAY_HUD_INFO: fetch refused: " + fetch.ToJsonString()); return false; }
        for (var i = 0; i < 2400 && (host.RunningGoal(CommandHost.CompanionAvatarId) != null || host.HeldBy(CommandHost.CompanionAvatarId) == null); i++) await Frames(1);
        return host.HeldBy(CommandHost.CompanionAvatarId) == "obj:doorstop" && host.RunningGoal(CommandHost.CompanionAvatarId) == null;
    }

    /// <summary>Bare floor (no room thing) between min and max metres from the Gubble, inside the room, or null.</summary>
    private Vector3? BareFloorAway(float min, float max)
    {
        var host = CommandHost.Of(_world)!;
        var at = _world.Companion.GlobalPosition;
        for (var d = min; d <= max; d += 0.1f)
            for (var i = 0; i < 16; i++)
            {
                var spot = at + new Vector3(Mathf.Sin(i * Mathf.Pi / 8), 0, Mathf.Cos(i * Mathf.Pi / 8)) * d;
                if (!host.Room.Bounds.Grow(-0.1f).HasPoint(new Vector3(spot.X, host.Room.Bounds.GetCenter().Y, spot.Z))) continue;
                _hud.AimRayForTests = (spot + Vector3.Up * 0.3f, spot + Vector3.Down * 0.5f);
                var aim = _hud.AimNow();
                if (aim.Hit && !aim.AtGubble && aim.Entity == null && aim.Point.Y < 0.02f && at.DistanceTo(aim.Point) >= min) return aim.Point;
            }
        return null;
    }

    /// <summary>
    /// The Gubble puts things down (the founder's playtest: "there's not a simple command for that yet"). While it holds something
    /// its bubble says what; a tap aimed at the Gubble is "put it down here" and at a spot "put it there" (it goes there first); the
    /// wheel's Fetch is Put down; each through the host's entity.release, as the player.
    /// </summary>
    private async Task TestPutDown()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.SetViewMode(1);
        _hud.LightForTests = _ => 0.9f;
        var empty = host.PlayerPutDown();
        Check(!_hud.GubbleHolds && !empty["ok"]!.GetValue<bool>() && empty["error"]?["message"]?.GetValue<string>() == "This avatar is not holding anything.",
            "with empty hands there is nothing to put down: the host says so (entity.release, as the player, through the Gubble's avatar)");
        Check(await GubbleFetchesDoorstop(), "the Gubble fetches the doorstop and comes back holding it");
        _hud.Cue.Clear();
        await Frames(2);
        var name = host.HeldName(CommandHost.CompanionAvatarId);
        Check(_hud.GubbleHolds && name == "Doorstop" && _hud.Cue.Bubble.Text == $"( {GubbleMagic.FetchIcon} {name} )",
            $"while it holds something, its bubble says what: \"{_hud.Cue.Bubble.Text}\"");
        _hud.AimRayForTests = AtGubble();
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.PutHere && _hud.AskTag.Text == "right-click: ↓ put it down here",
            $"aimed at the Gubble while it holds something, a tap will have it put it down here: \"{_hud.AskTag.Text}\"");
        if (BareFloorAway(0.5f, 1.2f) is not { } spot)
        {
            Check(false, "a bare floor spot half a metre or more from the Gubble, to point at");
            return;
        }
        _hud.AimRayForTests = Down(spot);
        await Frames(10);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.PutThere && _hud.AskTag.Text == "right-click: ↓ put it there",
            $"aimed at a spot, a tap will have it put it there: \"{_hud.AskTag.Text}\"");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var wedge = _hud.Wheel.WedgeLabels[GubbleMagic.Wedges.ToList().FindIndex(w => w.Kind == GubbleMagic.WedgeKind.Fetch)].Text;
        _hud._UnhandledInput(RightButton(false));
        Check(wedge == $"{GubbleMagic.PutDownIcon}\n{GubbleMagic.PutDownName}" && !GetTree().Paused, "and the wheel's Fetch wedge is Put down: " + wedge.Replace("\n", " "));

        Tap();
        Check(_hud.PutWaiting && companion.CurrentIntent == "go_to" && _hud.GubbleHolds,
            $"a tap on a spot {companion.GlobalPosition.DistanceTo(spot):0.00} m away, out of its reach: it sets off there, still holding the doorstop");
        for (var i = 0; i < 1800 && _hud.PutWaiting; i++) await Frames(1);
        var put = Vec(Entity(host, "obj:doorstop")["position_m"]!);
        Check(!_hud.PutWaiting && !_hud.GubbleHolds && PlanarDistance(put, spot) < 0.12f && _hud.Cue.State == "done",
            $"and there it puts the doorstop down where you pointed ({PlanarDistance(put, spot):0.00} m from the spot)");
        await Frames(2);
        Check(_hud.Cue.Bubble.Text != $"( {GubbleMagic.FetchIcon} Doorstop )", "its bubble no longer says it holds the doorstop");

        Check(await GubbleFetchesDoorstop(), "it fetches the doorstop again");
        _hud.AimRayForTests = AtGubble();
        _hud._UnhandledInput(RightButton(true));
        _hud._UnhandledInput(Motion(new Vector2(-50, 50)));
        _hud._UnhandledInput(RightButton(false));
        await Frames(10);
        var down = Vec(Entity(host, "obj:doorstop")["position_m"]!);
        Check(!_hud.GubbleHolds && host.HeldBy(CommandHost.CompanionAvatarId) == null && PlanarDistance(down, companion.GlobalPosition) < 0.3f && _hud.Cue.State == "done",
            $"the wheel's Put down, aimed at the Gubble, puts it down in front of it ({PlanarDistance(down, companion.GlobalPosition):0.00} m away)");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        wedge = _hud.Wheel.WedgeLabels[GubbleMagic.Wedges.ToList().FindIndex(w => w.Kind == GubbleMagic.WedgeKind.Fetch)].Text;
        _hud._UnhandledInput(RightButton(false));
        Check(wedge == $"{GubbleMagic.FetchIcon}\nFetch", "with its hands empty again the wedge is Fetch");
        _hud._UnhandledInput(Press(Key.X));
        _hud.AimRayForTests = Air();
    }

    private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>A colour change recolours a running glow at once: the same effect, the new aura.</summary>
    private async Task TestAuraRecolours()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        var look = _world.Look;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        _hud._UnhandledInput(Press(Key.Key1));
        _hud.LightForTests = _ => 0.2f;
        _hud.AimRayForTests = Down(FloorNearGubble(0.3f));
        _hud._UnhandledInput(Press(Key.Key1));
        var ids = host.ActiveEffectIds.ToArray();
        var before = companion.AuraColor!.Value;
        Check(ids.Length == 2 && ids.All(id => look.GlowLight(id)!.LightColor.IsEqualApprox(GlowLook.LightColor(before))) && companion.LastCastToward != null,
            "a halo on the Gubble and a wisp at a dark spot (1 aimed there) both take the Gubble's aura colour");
        // The Gubble's colour is the team's now (chosen in the menu's New team panel, worn through RoomHud.UseTeam).
        var other = GarageGame.Native.Saves.TeamFile.Create("aura_team", new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), "Mira", "prototype", companion.CompanionName, "#d28f63");
        _hud.UseTeam(other);
        await Frames(2);
        var after = companion.AuraColor!.Value;
        var colour = GlowLook.LightColor(after);
        bool Recoloured(string id) => look.GlowRoot(id) is { } root && look.GlowLight(id)!.LightColor.IsEqualApprox(colour) &&
            root.GetNode<MeshInstance3D>("Halo").MaterialOverride is StandardMaterial3D halo && new Color(halo.AlbedoColor, 1).IsEqualApprox(new Color(colour, 1)) &&
            (root.GetNodeOrNull<MeshInstance3D>("Core")?.MaterialOverride is not StandardMaterial3D core || core.Emission.IsEqualApprox(colour));
        Check(!after.IsEqualApprox(before) && after.IsEqualApprox(companion.AppearanceColor), $"a team's colour changes the Gubble's aura with its colour ({before.ToHtml(false)} to {after.ToHtml(false)})");
        Check(host.ActiveEffectIds.SequenceEqual(ids) && ids.All(Recoloured), "and both running glows take the new colour at once: the light, its halo, the wisp's heart, the same effects");
        for (var i = 0; i < 4; i++) _hud.RecolourGlows();
        // Back to the profile's colour.
        _hud.UseTeam(other with { GubbleColour = "#a18cc3" });
        Check(companion.AppearanceColor.ToHtml(false) == "a18cc3", "(the colour put back)");
        _hud._UnhandledInput(Press(Key.X));
        _hud.LightForTests = _ => 0.9f;
    }

    /// <summary>has_used_glow in the profile: a fresh HUD reading it knows; a missing or wrong-typed value is false.</summary>
    private async Task TestProfileField()
    {
        Check(ProfileValue("has_used_glow").VariantType == Variant.Type.Bool && ProfileValue("has_used_glow").AsBool(), "the profile on disk says has_used_glow = true");
        async Task<bool> Fresh()
        {
            var world = new Node3D();
            AddChild(world);
            var player = new SmallPlayerController { ReadKeyboard = false };
            world.AddChild(player);
            var companion = new CompanionAvatar();
            world.AddChild(companion);
            var hud = new RoomHud { Player = player, Companion = companion, RoomTitle = "PROFILE" };
            world.AddChild(hud);
            await Frames(2);
            var used = hud.HasUsedGlow;
            world.QueueFree();
            await Frames(1);
            return used;
        }
        Check(await Fresh(), "a new session reading that profile knows Glow was cast (the dusk moment will not show)");
        WriteProfile(new Godot.Collections.Dictionary { ["version"] = 1, ["companion_color"] = ProfileGubbleColour, ["has_used_glow"] = "yes" });
        Check(!await Fresh(), "a has_used_glow that is not a boolean is not believed");
        WriteProfile(new Godot.Collections.Dictionary { ["version"] = 1, ["companion_color"] = ProfileGubbleColour });
        Check(!await Fresh(), "and a profile without it (every profile before this round) means never cast");
    }

    private static System.Text.Json.Nodes.JsonObject Entity(CommandHost host, string id) => host.Entities().First(e => e["id"]!.GetValue<string>() == id);

    private static Vector3 Vec(System.Text.Json.Nodes.JsonNode node) => new((float)node[0]!.GetValue<double>(), (float)node[1]!.GetValue<double>(), (float)node[2]!.GetValue<double>());

    private async Task Face(SmallPlayerController body, Vector3 facing)
    {
        body.Rotation = new Vector3(0, Mathf.Atan2(-facing.X, -facing.Z), 0);
        await Frames(4);
    }

    /// <summary>Walk a body to a spot on the floor plane, steering each tick: running while far, slowing near it.</summary>
    private async Task<bool> WalkTo(SmallPlayerController body, Vector2 target, int maxFrames = 900)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            var to = target - new Vector2(body.GlobalPosition.X, body.GlobalPosition.Z);
            if (to.Length() < 0.008f)
            {
                body.SetControlInput(Vector2.Zero);
                await Frames(20);
                return true;
            }
            body.Rotation = new Vector3(0, Mathf.Atan2(-to.X, -to.Y), 0);
            body.SetControlInput(new Vector2(0, to.Length() > 0.1f ? 1 : 0.35f), sprint: to.Length() > 0.3f);
            await Frames(1);
        }
        body.SetControlInput(Vector2.Zero);
        return false;
    }

    private static void RemoveSaves()
    {
        foreach (var path in new[] { HudSaves, HudProfile.GetBaseDir() })
        {
            var directory = ProjectSettings.GlobalizePath(path);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    private static void WriteProfile(Godot.Collections.Dictionary values)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(HudProfile.GetBaseDir()));
        var config = new ConfigFile();
        foreach (var (key, value) in values) config.SetValue("profile", key.AsString(), value);
        config.Save(HudProfile);
    }

    private static Variant ProfileValue(string key)
    {
        var config = new ConfigFile();
        return config.Load(HudProfile) == Error.Ok ? config.GetValue("profile", key, new Variant()) : new Variant();
    }

    private async Task TestFoldedHelp()
    {
        var footer = _hud.GetNode<PanelContainer>("HelpFooter");
        var column = footer.GetChild<VBoxContainer>(0);
        var help = column.GetNode<VBoxContainer>("KeyHelp");
        var hint = column.GetChild<Label>(0);
        var foldedHeight = footer.Size.Y;
        Check(!help.Visible && hint.Text == "H keys", "key help starts folded with one short H hint");
        // The second recorded play: the line at the bottom edge was never seen. The footer keeps the key hints only (PlayHudTest.Notice.cs).
        Check(footer.FindChild("Notice", true, false) == null && column.GetChildCount() == 2 && _hud.FindChild("Notice", true, false) is Label { } moved && moved.GetParent() == _hud.NoticeCard,
            "the bottom line keeps the key hints only: the notice is no longer in it but in its own card");
        _hud._UnhandledInput(Press(Key.H));
        await Frames(3);
        Check(help.IsVisibleInTree() && help.GetChildren().OfType<Label>().All(l => l.IsVisibleInTree()), "H unfolds every key-help line");
        Check(footer.Size.Y > foldedHeight, "the footer grows to fit the unfolded help");
        var repeat = Press(Key.H); repeat.Echo = true;
        _hud._UnhandledInput(repeat);
        Check(help.Visible, "holding H does not repeatedly toggle the help");
        _hud._UnhandledInput(Press(Key.H));
        await Frames(3);
        Check(!help.Visible && hint.Text == "H keys", "H refolds the key help");
        Check(Mathf.Abs(footer.Size.Y - foldedHeight) < 1, "the folded footer releases the space used by the full help");
    }

    private void TestWorkshopGone()
    {
        var texts = _world.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text).ToArray();
        Check(!texts.Any(t => t.Contains("INVENTIONS", StringComparison.Ordinal) || t.Contains("worn design", StringComparison.Ordinal)), "the room shows no INVENTIONS panel");
        var help = string.Join("\n", texts);
        Check(!help.Contains("[/]", StringComparison.Ordinal) && help.Contains("T time of day", StringComparison.Ordinal) && help.Contains("Shift+T season", StringComparison.Ordinal),
            "the help lines name T and Shift+T, and no longer [ and ] (the isometric turn is gone)");
        Check(!help.Contains("B Build", StringComparison.Ordinal), "and say nothing of the retired build keys");
    }

    /// <summary>T: seven times of day on the day shown (dawn to night, in order, lighter by day), then the real clock.</summary>
    private async Task TestTimeOfDay()
    {
        var look = _world.Look;
        Check(_hud.TimeStop == -1 && _hud.SeasonStop == -1 && look.ClockNote.StartsWith("real clock", StringComparison.Ordinal), "the room starts on the real clock and calendar: " + look.ClockNote);
        Check(_hud.ClockText().Contains("real clock", StringComparison.Ordinal) && _hud.ClockText().Contains("real date", StringComparison.Ordinal), "the HUD says so: " + _hud.ClockText());
        var hours = new float[RoomHud.TimeStopNames.Length];
        var daylight = new float[hours.Length];
        for (var stop = 0; stop < hours.Length; stop++)
        {
            _hud._UnhandledInput(Press(Key.T));
            await Frames(2);
            hours[stop] = look.Moment.Hour;
            daylight[stop] = look.Moment.Daylight;
            Check(_hud.TimeStop == stop && Mathf.Abs(hours[stop] - RoomHud.TimeStopHour(look.Preset, stop, look.Moment.DayOfYear)) < 0.01f && look.ClockNote.Contains("hour pinned", StringComparison.Ordinal),
                $"T step {stop + 1} is {RoomHud.TimeStopNames[stop]} ({hours[stop]:0.00} h, daylight {daylight[stop]:0.00}); the look is pinned to it");
            Check(_hud.ClockText().Contains(RoomHud.TimeStopNames[stop], StringComparison.Ordinal) && _hud.ClockText().StartsWith($"{(int)hours[stop]:00}:", StringComparison.Ordinal), "the HUD shows that time: " + _hud.ClockText());
        }
        // In order through the day (an hour past midnight for night is allowed to wrap), light by day and dark at night.
        var unwrapped = hours.Select((h, i) => i > 0 && h < hours[0] ? h + 24f : h).ToArray();
        Check(unwrapped.Zip(unwrapped.Skip(1), (a, b) => b > a).All(ok => ok), "the seven times run in order: " + string.Join(", ", hours.Select(h => h.ToString("0.0"))));
        // Noon within 0.02 of the brightest: the day's light keys plateau through the afternoon, and on some real dates (mid
        // October: noon 0.89, late afternoon 0.90) the late-afternoon step reads a hundredth brighter, which no one can see.
        Check(daylight[2] >= daylight[1] && daylight[2] >= daylight[3] - 0.02f && daylight[3] > daylight[4] && daylight[4] > daylight[6], $"noon is the brightest and the light falls through the afternoon to sunset ({string.Join(" ", daylight.Select(d => d.ToString("0.00")))})");
        Check(daylight[6] < 0.1f && daylight[5] <= daylight[4] && look.Moment.MoonWeight > 0.9f, "night is dark and lit by the moon");
        Check(look.LampsOn, "and the lamps are on at night");
        _hud._UnhandledInput(Press(Key.T));
        await Frames(2);
        Check(_hud.TimeStop == -1 && look.ClockNote.StartsWith("real clock", StringComparison.Ordinal), "one more T returns to the real clock: " + look.ClockNote);
    }

    /// <summary>Shift+T: the four solstices and equinoxes in order, then the real calendar.</summary>
    private async Task TestSeason()
    {
        var look = _world.Look;
        var seasons = new[] { "spring", "summer", "autumn", "winter" };
        var lengths = new float[4];
        for (var stop = 0; stop < RoomHud.SeasonStops.Length; stop++)
        {
            _hud._UnhandledInput(Press(Key.T, shift: true));
            await Frames(2);
            lengths[stop] = LookClock.DayLength(look.Preset, look.Moment.DayOfYear);
            Check(_hud.SeasonStop == stop && look.Moment.DayOfYear == RoomHud.SeasonStops[stop].DayOfYear && look.Moment.Season == seasons[stop],
                $"Shift+T step {stop + 1} is the {RoomHud.SeasonStops[stop].Name} (day {look.Moment.DayOfYear}, {look.Moment.Season}, a {lengths[stop]:0.0} h day)");
            Check(_hud.ClockText().Contains(RoomHud.SeasonStops[stop].Name, StringComparison.Ordinal) && _hud.ClockText().Contains(look.Moment.Season, StringComparison.Ordinal), "the HUD shows it: " + _hud.ClockText());
            Check(_hud.TimeStop == -1, "stepping the season does not step the time");
        }
        Check(lengths[1] > lengths[0] && lengths[0] > lengths[3] && lengths[2] > lengths[3], "the June day is the longest and the December day the shortest");
        _hud._UnhandledInput(Press(Key.T, shift: true));
        await Frames(2);
        Check(_hud.SeasonStop == -1 && look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal), "one more Shift+T returns to the real calendar: " + look.ClockNote);
    }

    /// <summary>The two steps together: each time of day is found on the pinned date, and releasing one leaves the other pinned.</summary>
    private async Task TestClockCombined()
    {
        var look = _world.Look;
        _hud._UnhandledInput(Press(Key.T, shift: true));
        _hud._UnhandledInput(Press(Key.T, shift: true));
        _hud._UnhandledInput(Press(Key.T));
        _hud._UnhandledInput(Press(Key.T));
        _hud._UnhandledInput(Press(Key.T));
        await Frames(2);
        var june = RoomHud.SeasonStops[1].DayOfYear;
        Check(look.Moment.DayOfYear == june && Mathf.Abs(look.Moment.Hour - RoomHud.TimeStopHour(look.Preset, 2, june)) < 0.01f,
            $"noon on the June solstice is found from that day's own sun, not the real date's ({look.Moment.Hour:0.00} h)");
        var (rise, set) = LookClock.SunTimes(look.Preset.Tuning.Sun, june);
        Check(Mathf.Abs(look.Moment.Hour - (rise + set) * 0.5f) < 0.01f && look.Moment.SunElevationDeg > 70f, $"which is high sun (elevation {look.Moment.SunElevationDeg:0} degrees)");
        for (var i = 0; i < 5; i++) _hud._UnhandledInput(Press(Key.T));
        await Frames(2);
        Check(_hud.TimeStop == -1 && _hud.SeasonStop == 1 && look.Moment.DayOfYear == june && !look.ClockNote.Contains("real calendar", StringComparison.Ordinal),
            "back on the real clock the date stays on the June solstice");
        // Esc-style safety: keys do nothing while the appearance panel owns the keyboard.
        _hud._UnhandledInput(Press(Key.C));
        var stop = _hud.TimeStop;
        _hud._UnhandledInput(Press(Key.T));
        Check(_hud.SettingsOpen && _hud.TimeStop == stop, "T does nothing while the appearance panel is open");
        _hud._UnhandledInput(Press(Key.C));
        for (var i = 0; i < 3; i++) _hud._UnhandledInput(Press(Key.T, shift: true));
        await Frames(2);
        Check(_hud.SeasonStop == -1 && _hud.TimeStop == -1 && look.ClockNote.StartsWith("real clock, real calendar", StringComparison.Ordinal), "both released, the look follows the real clock again: " + look.ClockNote);
    }

    private async Task TestNoLookNoClock()
    {
        var world = new Node3D();
        AddChild(world);
        var player = new SmallPlayerController { ReadKeyboard = false };
        world.AddChild(player);
        var companion = new CompanionAvatar();
        world.AddChild(companion);
        var hud = new RoomHud { Player = player, Companion = companion, RoomTitle = "NO LOOK" };
        world.AddChild(hud);
        await Frames(2);
        hud._UnhandledInput(Press(Key.T));
        hud._UnhandledInput(Press(Key.T, shift: true));
        Check(hud.TimeStop == -1 && hud.SeasonStop == -1 && hud.ClockText().Length == 0, "without a look the clock keys do nothing and the HUD shows no clock");
        world.QueueFree();
    }

    /// <summary>
    /// The name tag over the companion was see-through: depth of field read the depth behind it and blurred it, and temporal
    /// anti-aliasing smeared it in motion. Drawn with an alpha cut it writes depth and motion like a solid, so both treat it as an object.
    /// </summary>
    private void TestNameTag()
    {
        var label = _world.Companion.GetNode<Label3D>("CompanionLabel");
        Check(label.AlphaCut != Label3D.AlphaCutMode.Disabled, $"the name tag is drawn solid (alpha cut {label.AlphaCut}), not as see-through blending");
        Check(!label.NoDepthTest && label.Billboard == BaseMaterial3D.BillboardModeEnum.Enabled, "it still faces the camera and hides behind what is in front of it");
    }

    private async Task TestNameTagSize()
    {
        // An isolated, stationary fixture avoids moving the room's avatars or changing its cameras.
        var fixture = new Node3D();
        AddChild(fixture);
        var companion = new CompanionAvatar();
        fixture.AddChild(companion);
        companion.SetPhysicsProcess(false);
        var camera = new Camera3D { Near = 0.005f };
        fixture.AddChild(camera);
        camera.MakeCurrent();
        var label = companion.GetNode<Label3D>("CompanionLabel");
        Check(label.FixedSize, "the tag uses distance-independent rendering");
        foreach (var (view, distance, fov) in new[]
        {
            ("F2 close", 0.26f, 68f), ("F2", 0.5f, 68f), ("F2 orbit", RoomHud.OrbitFromM, RoomHud.ShoulderFovDeg), ("F2 far", RoomHud.ShoulderFarM, RoomHud.ShoulderFovDeg),
            ("F3 near", RoomHud.DioramaMinDistanceM, RoomHud.DioramaFovDeg),
            ("F3", RoomHud.DioramaDefaultDistanceM, RoomHud.DioramaFovDeg),
            ("F3 far", RoomHud.DioramaMaxDistanceM, RoomHud.DioramaFovDeg),
            ("F4", RoomHud.IsoDistanceM, RoomHud.IsoFovDeg)
        })
        {
            camera.Position = Vector3.Back * distance;
            camera.Fov = fov;
            await Frames(3);
            var fraction = ProjectedTagHeight(label, camera);
            Check(label.Visible && fraction > 0.015f && fraction <= 0.03f,
                $"{view}: tag is readable and below 3% screen height at {distance:0.00} m ({fraction * 100:0.00}%)");
        }
        // Include the maximum allowed name length so its new glyph bounds are also sized.
        companion.SetDisplayName(new string('W', 40));
        await Frames(3);
        Check(ProjectedTagHeight(label, camera) <= 0.03f, "a renamed tag stays below 3% screen height");
        foreach (var distance in new[] { 0.249f, 0.1f, 0.01f })
        {
            camera.Position = Vector3.Back * distance;
            await Frames(2);
            Check(!label.Visible, $"the tag hides inside 0.25 m ({distance:0.000} m)");
        }
        camera.Position = Vector3.Back * 0.251f;
        await Frames(2);
        Check(label.Visible && ProjectedTagHeight(label, camera) <= 0.03f, "the tag reappears small just outside 0.25 m");
        _hud.SetViewMode(1);
        fixture.QueueFree();
    }

    // Label3D.FixedSize renders its geometry as if its origin were one metre into the view.
    // Project its actual bounds (with an outline allowance) using the camera's billboard axes;
    // this checks projected size without needing GPU pixels in the headless suite.
    private static float ProjectedTagHeight(Label3D label, Camera3D camera)
    {
        var screen = camera.GetViewport().GetVisibleRect();
        var origin = camera.ProjectPosition(screen.GetCenter(), 1);
        var bounds = label.GetAabb();
        var outline = label.OutlineSize * label.PixelSize;
        var up = camera.GetCameraTransform().Basis.Y.Normalized() * label.GlobalBasis.Scale.Y;
        var top = camera.UnprojectPosition(origin + up * (bounds.End.Y + outline));
        var bottom = camera.UnprojectPosition(origin + up * (bounds.Position.Y - outline));
        return Mathf.Abs(top.Y - bottom.Y) / screen.Size.Y;
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (!condition) _failures++;
        GD.Print($"{(condition ? "PASS" : "FAIL")}: {label}");
    }
}
