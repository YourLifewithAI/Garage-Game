using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// The player's controls by name. Every binding is an input action in game/project.godot ([input]), bound to a physical key
/// (a position on the keyboard, whatever the layout), so the player can remap it and the key help follows. The handlers
/// (RoomHud, SmallPlayerController) ask for these actions and never for a keycode.
/// </summary>
public static class Act
{
    // Movement, read every physics tick.
    public static readonly StringName MoveForward = "move_forward";
    public static readonly StringName MoveBack = "move_back";
    public static readonly StringName MoveLeft = "move_left";
    public static readonly StringName MoveRight = "move_right";
    public static readonly StringName MoveSprint = "move_sprint";
    public static readonly StringName MoveDive = "move_dive";
    /// <summary>Jump on the ground; rise while diving.</summary>
    public static readonly StringName Jump = "jump";
    // The body.
    public static readonly StringName BodyRecover = "body_recover";
    public static readonly StringName BodyHome = "body_home";
    // The views: first person (F1) and third person over the shoulder (F2, the default). The diorama (F3) and isometric (F4)
    // views are shelved (the founder, 13 October): their keys, and [ and ] with them, are unbound and stay free.
    public static readonly StringName ViewEye = "view_eye";
    public static readonly StringName ViewShoulder = "view_shoulder";
    public static readonly StringName ViewObserve = "view_observe";
    /// <summary>F11: the window in full screen and back (the founder, 16 October, decision 66); the choice is kept in the machine's settings for the next launch.</summary>
    public static readonly StringName ViewFullscreen = "view_fullscreen";
    /// <summary>F2 only: the wheel zooms the over-the-shoulder view in, towards the shoulder.</summary>
    public static readonly StringName ViewZoomIn = "view_zoom_in";
    /// <summary>F2 only: the wheel zooms the over-the-shoulder view out, to look around.</summary>
    public static readonly StringName ViewZoomOut = "view_zoom_out";
    // The companion (the magic design's "The keys", step 2). Enter is kept free for the wish box.
    /// <summary>Tap: the smart ask at the aim. Hold: the Gubble wheel.</summary>
    public static readonly StringName GubbleAsk = "gubble_ask";
    /// <summary>Come, then follow (the recall).</summary>
    public static readonly StringName GubbleRecall = "gubble_recall";
    /// <summary>Stop the Gubble's goal and its effects (goal.stop).</summary>
    public static readonly StringName GubbleStop = "gubble_stop";
    /// <summary>The six ability slots, fixed by category (GubbleSlots): 1 light, 2 growth, 3 float, 4 burst, 5 build, 6 fire (decision 77).</summary>
    public static readonly StringName GubbleSlot1 = "gubble_slot_1";
    public static readonly StringName GubbleSlot2 = "gubble_slot_2";
    public static readonly StringName GubbleSlot3 = "gubble_slot_3";
    public static readonly StringName GubbleSlot4 = "gubble_slot_4";
    public static readonly StringName GubbleSlot5 = "gubble_slot_5";
    /// <summary>6: Fire (decisions 77 and 83): a tap lights the fire where you point at once, a drag (once Fireline is learned) a line.</summary>
    public static readonly StringName GubbleSlot6 = "gubble_slot_6";
    /// <summary>The ability slot actions in slot order (index 0 is slot 1): the six by category, the bar's first six keys.</summary>
    public static readonly StringName[] GubbleSlots = { GubbleSlot1, GubbleSlot2, GubbleSlot3, GubbleSlot4, GubbleSlot5, GubbleSlot6 };
    /// <summary>The hotbar's seventh to ninth keys, 7 to 9 (docs/game/HOTBAR.md): pocket slots 7 to 9, or the magic still to come.</summary>
    public static readonly StringName GubbleSlot7 = "gubble_slot_7";
    public static readonly StringName GubbleSlot8 = "gubble_slot_8";
    public static readonly StringName GubbleSlot9 = "gubble_slot_9";
    /// <summary>The hotbar's tenth key, 0 (slot 10: the tenth pocket slot, or magic still to come).</summary>
    public static readonly StringName GubbleSlot10 = "gubble_slot_10";
    /// <summary>
    /// The hotbar's ten keys in slot order (index 0 is slot 1; keys 1 to 0, decision 119): on the pockets' layer each chooses its pocket slot,
    /// on the magic layer the first six are the abilities (GubbleSlots) and the rest the slots to come. (The map's names are the ability slots'
    /// from before the bar: gubble_slot_1 to gubble_slot_10.)
    /// </summary>
    public static readonly StringName[] BarSlots = { GubbleSlot1, GubbleSlot2, GubbleSlot3, GubbleSlot4, GubbleSlot5, GubbleSlot6, GubbleSlot7, GubbleSlot8, GubbleSlot9, GubbleSlot10 };
    // The hands.
    /// <summary>Pick up what the player faces, or put down what it holds.</summary>
    public static readonly StringName Hands = "hands";
    public static readonly StringName Push = "push";
    /// <summary>
    /// E, the one interaction key for the world (decisions 86 and 90): sit by a lit campfire and stand up again, pick up a Fractal or a
    /// thing, set down or gather what the hands hold (RoomHud.Interact.cs). Its map name is from when E only gathered (item.gather, the
    /// mechanics round's decision 26): the input map keeps it, as it keeps hud_customize and mouse_capture.
    /// </summary>
    public static readonly StringName Interact = "item_gather";
    /// <summary>Z: sit down by a lit campfire to rest, and stand up again (Fire's camp, decision 70); moving stands up too. A second key for the camp this round (decision 90).</summary>
    public static readonly StringName Rest = "rest";
    // The testing toggles (the help lists them under "Testing").
    public static readonly StringName PhysicsNext = "physics_next";
    public static readonly StringName TimeStep = "time_step";
    public static readonly StringName SeasonStep = "season_step";
    public static readonly StringName Lamps = "lamps";
    /// <summary>Steps the drawn character's bounce through 0.5x, 1x and 1.5x of its profile (Characters/Gaits/BounceKey).</summary>
    public static readonly StringName BounceStep = "bounce_step";
    /// <summary>Prints every value of the drawn character's motion profile to the log.</summary>
    public static readonly StringName BouncePrint = "bounce_print";
    // The panels and the mouse. The cursor is always free (the Glow playtest, 9 October): it is the aim in every view.
    public static readonly StringName HudHelp = "hud_help";
    /// <summary>C: the settings panel (it was the Customize panel; the input action keeps its name, hud_customize, until project.godot renames it).</summary>
    public static readonly StringName HudCustomize = "hud_customize";
    /// <summary>M: the Magic panel (the Gubble's magic: Grow, Fire on the click, Glow's lights, wishes), opened and closed again.</summary>
    public static readonly StringName HudMagic = "hud_magic";
    /// <summary>The journal, the leather book at the lower right: opens it at its index, and closes it again.</summary>
    public static readonly StringName Journal = "journal";
    /// <summary>
    /// Tab: the hotbar's layer switch (docs/game/HOTBAR.md; RoomHud.Hotbar.cs), the pockets brought down or the Gubble's magic. Its map name
    /// is from when Tab opened the pockets panel, which the bar retired: the input map keeps it, as it keeps item_gather.
    /// </summary>
    public static readonly StringName BarLayer = "pockets_panel";
    /// <summary>The eye icon above the journal: look through the Gubble's eyes, and back to the player's own.</summary>
    public static readonly StringName GubbleEyes = "gubble_eyes";
    /// <summary>Esc: first lets go of what is going on (a panel, the journal, the Gubble's eyes, the wheel, a drag, a chosen bubble), then opens the menu (decisions 81 and 97). (Its map name is from when it freed the captured mouse.)</summary>
    public static readonly StringName MouseRelease = "mouse_release";
    /// <summary>Hold and drag to look around (the left button); a click without a drag does nothing. (Its map name is from when a click captured the mouse.)</summary>
    public static readonly StringName LookDrag = "mouse_capture";
    // The playtest record (docs/runs/RUN-2-PLAYTEST-LOOP.md): F6 to F9 are free here and bound to nothing in Windows.
    /// <summary>F9: "something's off here", a still and a mark.</summary>
    public static readonly StringName PlaytestMark = "playtest_mark";
    /// <summary>F8: "keep this", a still and a mark.</summary>
    public static readonly StringName PlaytestKeep = "playtest_keep";
    /// <summary>F7: the steady stills faster (off, 30, 10, 3, 1 s).</summary>
    public static readonly StringName PlaytestStillsFaster = "playtest_stills_faster";
    /// <summary>F6: the steady stills slower.</summary>
    public static readonly StringName PlaytestStillsSlower = "playtest_stills_slower";
}

/// <summary>Where an action is live. The keys of one context must not share a key; two contexts may.</summary>
[Flags]
public enum ControlContext
{
    /// <summary>Every view, while the settings panel is shut.</summary>
    Always = 1,
    /// <summary>The over-the-shoulder view (F2) only.</summary>
    Shoulder = 2,
    /// <summary>While the settings panel is open, which blocks every key but these.</summary>
    Customize = 8,
    /// <summary>While the journal is open, opening or closing, which blocks every key but these (RoomHud.Journal.cs).</summary>
    Reading = 16,
    /// <summary>While the player looks through the Gubble's eyes, which blocks every key but these (RoomHud.GubbleView.cs).</summary>
    Looking = 32,
}

/// <summary>One action and where it is live.</summary>
public readonly record struct ControlSpec(StringName Action, ControlContext Live);

/// <summary>
/// Reading the controls: whether an event is an action's press, whether an action is held, and what key an action has now
/// (for the key help). Mouse motion stays event-based; actions are for buttons, keys and the wheel.
/// </summary>
public static class PlayerControls
{
    /// <summary>
    /// The gather key's binding, made here when the project's input map does not carry it yet (project.godot is the integrator's: its
    /// [input] section holds item_gather on E once they apply the packet's diff, and then this does nothing). The deadzone and
    /// the key are the ones the diff gives it.
    /// </summary>
    public static void EnsureDefaults()
    {
        var changed = false;
        if (!InputMap.HasAction(Act.Interact))
        {
            InputMap.AddAction(Act.Interact, 0.2f);
            InputMap.ActionAddEvent(Act.Interact, new InputEventKey { PhysicalKeycode = Key.E });
            changed = true;
        }
        // The rest key likewise (Fire's camp): Z, until project.godot carries the packet's rest action.
        if (!InputMap.HasAction(Act.Rest))
        {
            InputMap.AddAction(Act.Rest, 0.2f);
            InputMap.ActionAddEvent(Act.Rest, new InputEventKey { PhysicalKeycode = Key.Z });
            changed = true;
        }
        // Fire on the wheel (decision 77): 6, and the Magic panel's key, M, until project.godot carries the packet's two actions;
        // the bar's Tab and its keys 7 to 0 likewise (a fixture whose input map was built without project.godot's).
        foreach (var (action, key) in new[]
        {
            (Act.GubbleSlot6, Key.Key6), (Act.HudMagic, Key.M), (Act.BarLayer, Key.Tab),
            (Act.GubbleSlot7, Key.Key7), (Act.GubbleSlot8, Key.Key8), (Act.GubbleSlot9, Key.Key9), (Act.GubbleSlot10, Key.Key0),
        })
        {
            if (InputMap.HasAction(action)) continue;
            InputMap.AddAction(action, 0.2f);
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
            changed = true;
        }
        if (changed) MapChanged();
    }

    /// <summary>Every control and where it is live; the map in project.godot and this list name the same actions (PlayHudTest).</summary>
    public static readonly ControlSpec[] Specs =
    {
        new(Act.MoveForward, ControlContext.Always), new(Act.MoveBack, ControlContext.Always),
        new(Act.MoveLeft, ControlContext.Always), new(Act.MoveRight, ControlContext.Always),
        new(Act.MoveSprint, ControlContext.Always), new(Act.MoveDive, ControlContext.Always), new(Act.Jump, ControlContext.Always),
        new(Act.BodyRecover, ControlContext.Always), new(Act.BodyHome, ControlContext.Always),
        new(Act.ViewEye, ControlContext.Always), new(Act.ViewShoulder, ControlContext.Always),
        new(Act.ViewObserve, ControlContext.Always),
        // The window's own key is live everywhere, as the record's: the panels and the Gubble's eyes fill the screen too.
        new(Act.ViewFullscreen, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
        new(Act.ViewZoomIn, ControlContext.Shoulder), new(Act.ViewZoomOut, ControlContext.Shoulder),
        new(Act.GubbleAsk, ControlContext.Always), new(Act.GubbleRecall, ControlContext.Always), new(Act.GubbleStop, ControlContext.Always),
        new(Act.GubbleSlot1, ControlContext.Always), new(Act.GubbleSlot2, ControlContext.Always), new(Act.GubbleSlot3, ControlContext.Always),
        new(Act.GubbleSlot4, ControlContext.Always), new(Act.GubbleSlot5, ControlContext.Always), new(Act.GubbleSlot6, ControlContext.Always),
        new(Act.GubbleSlot7, ControlContext.Always), new(Act.GubbleSlot8, ControlContext.Always), new(Act.GubbleSlot9, ControlContext.Always),
        new(Act.GubbleSlot10, ControlContext.Always),
        new(Act.Hands, ControlContext.Always), new(Act.Push, ControlContext.Always), new(Act.Interact, ControlContext.Always),
        new(Act.Rest, ControlContext.Always),
        new(Act.PhysicsNext, ControlContext.Always), new(Act.TimeStep, ControlContext.Always),
        new(Act.SeasonStep, ControlContext.Always), new(Act.Lamps, ControlContext.Always),
        new(Act.BounceStep, ControlContext.Always), new(Act.BouncePrint, ControlContext.Always),
        new(Act.HudHelp, ControlContext.Always),
        new(Act.HudCustomize, ControlContext.Always | ControlContext.Customize),
        new(Act.HudMagic, ControlContext.Always | ControlContext.Customize),
        new(Act.BarLayer, ControlContext.Always),
        new(Act.Journal, ControlContext.Always | ControlContext.Customize | ControlContext.Reading),
        new(Act.GubbleEyes, ControlContext.Always | ControlContext.Customize | ControlContext.Looking),
        new(Act.MouseRelease, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
        new(Act.LookDrag, ControlContext.Always | ControlContext.Looking),
        // The record's keys are live everywhere: what is off may be on a panel.
        new(Act.PlaytestMark, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
        new(Act.PlaytestKeep, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
        new(Act.PlaytestStillsFaster, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
        new(Act.PlaytestStillsSlower, ControlContext.Always | ControlContext.Customize | ControlContext.Reading | ControlContext.Looking),
    };

    /// <summary>
    /// Pairs on one key whose modifiers nest: the first is checked before the second, so Shift+T is the season and a bare T the
    /// time of day. (Matching is not exact: Shift+T also satisfies a bare T, as it did when the keys were keycodes.)
    /// </summary>
    public static readonly (StringName First, StringName Second)[] CheckedBefore = { (Act.SeasonStep, Act.TimeStep), (Act.BouncePrint, Act.BounceStep) };

    /// <summary>When set (a test), every action a handler asks about is recorded here, to show the code uses the map's actions and no others.</summary>
    public static HashSet<string>? Trace { get; set; }

    /// <summary>The actions in the project's input map that are the game's own (the engine's ui_ actions are not).</summary>
    public static IEnumerable<string> MapActions() => InputMap.GetActions().Select(name => name.ToString()).Where(name => !name.StartsWith("ui_", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>
    /// The event as the actions read it. A key event carries the key's position (PhysicalKeycode) and what the layout makes of
    /// it (Keycode). Actions are bound to the position; an event with only the second (a synthetic one) is read as the first,
    /// as the handlers did before the actions.
    /// </summary>
    public static InputEvent Normalise(InputEvent input)
    {
        if (input is InputEventKey { PhysicalKeycode: Key.None } key && key.Keycode != Key.None)
        {
            var copy = (InputEventKey)key.Duplicate();
            copy.PhysicalKeycode = key.Keycode;
            return copy;
        }
        return input;
    }

    /// <summary>Whether this is a press that has just happened: pressed, and not a held key repeating.</summary>
    public static bool Fresh(InputEvent input) => input.IsPressed() && !input.IsEcho();

    /// <summary>Whether the event is a fresh press of the action. Modifiers are not matched exactly: Shift+T is also a T.</summary>
    public static bool Hit(InputEvent input, StringName action)
    {
        Trace?.Add(action.ToString());
        return input.IsActionPressed(action);
    }

    /// <summary>Whether the event is the action's button or key letting go (the ask button's tap and hold end on it).</summary>
    public static bool Released(InputEvent input, StringName action)
    {
        Trace?.Add(action.ToString());
        return input.IsActionReleased(action);
    }

    /// <summary>Whether the action is held now (polled).</summary>
    public static bool Held(StringName action)
    {
        Trace?.Add(action.ToString());
        return Input.IsActionPressed(action);
    }

    /// <summary>
    /// The action's key as the help shows it: "W", "Shift+T", "click", "wheel up", named on the player's own keyboard layout;
    /// "unbound" when it has no key.
    /// </summary>
    public static string Label(StringName action)
    {
        if (!InputMap.HasAction(action)) return "unbound";
        // The map's copy of the events is freed here, on the main thread, never left to the .NET finalizer (S12).
        var events = InputMap.ActionGetEvents(action);
        using var copy = (Godot.Collections.Array)events;
        foreach (var input in events)
            if (Describe(input) is { } text) return text;
        return "unbound";
    }

    // ---- the key names the HUD shows every frame (S12: read from the map once, not 60 times a frame) ----

    private static readonly Dictionary<StringName, string> KeyLabels = new();
    private static int _recheck;

    /// <summary>
    /// Bumped whenever the key names are read from the map anew (MapChanged, or a change Recheck found): what a frame built from
    /// the names can compare to know they moved on.
    /// </summary>
    public static int MapVersion { get; private set; }

    /// <summary>An action's key name as Label says it, kept until the map changes: what the HUD shows every frame.</summary>
    public static string KeyLabel(StringName action)
    {
        if (!KeyLabels.TryGetValue(action, out var label)) KeyLabels[action] = label = Label(action);
        return label;
    }

    /// <summary>Whoever binds a key anew calls this: every key name is read from the map again, at once.</summary>
    public static void MapChanged()
    {
        KeyLabels.Clear();
        MapVersion++;
    }

    /// <summary>
    /// One control's key read from the map again, in turn (the HUD calls this every frame): a binding changed without MapChanged
    /// still shows within one pass over the controls, well under a second.
    /// </summary>
    public static void Recheck()
    {
        var action = Specs[_recheck++ % Specs.Length].Action;
        if (KeyLabels.TryGetValue(action, out var kept) && Label(action) != kept) MapChanged();
    }

    /// <summary>The four movement keys together: "WASD" when each is one character (forward, left, back, right), else "W/A/S/D" with what they are.</summary>
    public static string MoveLabel()
    {
        var keys = new[] { Act.MoveForward, Act.MoveLeft, Act.MoveBack, Act.MoveRight }.Select(KeyLabel).ToArray();
        return keys.All(key => key.Length == 1) ? string.Concat(keys) : string.Join("/", keys);
    }

    /// <summary>The look drag as the help says it: "drag" when it is the left button, else its button or key and "drag" ("middle-click drag").</summary>
    public static string DragLabel()
    {
        var label = KeyLabel(Act.LookDrag);
        return label == "click" ? "drag" : label + " drag";
    }

    /// <summary>The zoom keys together: "wheel" when they are the wheel's two directions, else both ("U/I").</summary>
    public static string ZoomLabel()
    {
        var (zoomIn, zoomOut) = (KeyLabel(Act.ViewZoomIn), KeyLabel(Act.ViewZoomOut));
        return zoomIn == "wheel up" && zoomOut == "wheel down" ? "wheel" : $"{zoomIn}/{zoomOut}";
    }

    /// <summary>The desktop display servers can say what a key position is labelled; the others (headless among them) report an error if asked.</summary>
    private static bool KnowsKeyboardLayout => _knowsKeyboardLayout ??= DisplayServer.GetName() is "Windows" or "X11" or "Wayland" or "macOS";
    private static bool? _knowsKeyboardLayout;

    private static string? Describe(InputEvent input)
    {
        switch (input)
        {
            case InputEventKey key:
            {
                var position = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
                if (position == Key.None) return null;
                // The label on the key as this keyboard prints it: the key at W's place reads Z on a French one.
                var shown = KnowsKeyboardLayout ? DisplayServer.KeyboardGetKeycodeFromPhysical(position) : Key.None;
                var code = shown != Key.None ? shown : position;
                // A key that prints a character is named by it ("[" rather than "BracketLeft"); the others by the engine's name.
                var name = code == Key.Escape ? "Esc" : (long)code is >= 33 and <= 126 ? ((char)(long)code).ToString() : OS.GetKeycodeString(code);
                return Modifiers(key, position) + name;
            }
            case InputEventMouseButton mouse:
                return Modifiers(mouse, Key.None) + mouse.ButtonIndex switch
                {
                    MouseButton.Left => "click",
                    MouseButton.Right => "right-click",
                    MouseButton.Middle => "middle-click",
                    MouseButton.WheelUp => "wheel up",
                    MouseButton.WheelDown => "wheel down",
                    var other => $"mouse button {(int)other}",
                };
            default:
                return input.AsText();
        }
    }

    /// <summary>"Ctrl+Alt+Shift+Meta+" for the modifiers the event asks for, leaving out the one that is the key itself.</summary>
    private static string Modifiers(InputEventWithModifiers input, Key own) =>
        (input.CtrlPressed && own != Key.Ctrl ? "Ctrl+" : "") + (input.AltPressed && own != Key.Alt ? "Alt+" : "") +
        (input.ShiftPressed && own != Key.Shift ? "Shift+" : "") + (input.MetaPressed && own != Key.Meta ? "Meta+" : "");
}
