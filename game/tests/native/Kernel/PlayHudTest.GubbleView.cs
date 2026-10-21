using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Through the Gubble's eyes (the founder, 13 October), headless in the real room: the eye icon stands above the journal's
/// dock and can never take the focus; I or a click opens the live view from the Gubble's eye (its rig drawing every frame while
/// open, idle otherwise, with the Gubble's own cull mask), and I, Esc or a click return; while it is open every other key waits
/// and the body stands still; a drag turns and tilts the Gubble's eye within the AI's bounds without turning the Gubble; the view
/// changes no world state; the icon blinks when the AI's look succeeds and not when it is refused; and in F1 the player's body is
/// hidden from the player's own eye but drawn for the Gubble's. Headless nothing is drawn: the rig's state and masks are tested
/// here, a real picture in a windowed run (GubbleViewCapture).
/// </summary>
public partial class PlayHudTest
{
    private async Task TestGubbleView()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        var companion = _world.Companion;
        var icon = _hud.EyesIcon;
        var eye = _hud.GubbleEye;
        var view = _hud.GetNode<Control>("GubbleView");
        player.ReadKeyboard = false;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        _hud.SetViewMode(1);
        host.PlayerGoal("stay");
        await Frames(2);
        try
        {
            // The icon: just above the journal's book at the lower right, clear of the book and the help footer, never focusable.
            var dock = _hud.Book.Dock.GetGlobalRect();
            var at = icon.GetGlobalRect();
            var footer = _hud.GetNode<PanelContainer>("HelpFooter").GetGlobalRect();
            Check(icon.Visible && _hud.MaySeeThroughGubbleEyes && !at.Intersects(dock) && at.End.Y <= dock.Position.Y && Mathf.Abs(at.GetCenter().X - dock.GetCenter().X) < 1 && at.Position.X > footer.End.X,
                $"the eye icon stands above the journal's book, clear of it and of the help footer ({at}, book {dock}, footer to {footer.End.X})");
            Check(icon.FocusMode == Control.FocusModeEnum.None && icon.TooltipText == "See through the Gubble's eyes · I",
                "the icon can never take the keyboard focus, and names its key from the input map: " + icon.TooltipText);
            Check(eye.EyeViewport == null && !eye.Live && !view.Visible, "closed, the Gubble's eye draws nothing (its rig is not even made yet)");

            // I opens the live view: the rig draws every frame, from the Gubble's eye, with the Gubble's own cull mask.
            var room = WorldState(host);
            _hud._UnhandledInput(Press(Key.I));
            var viewport = eye.EyeViewport!;
            var camera = eye.EyeCamera!;
            Check(_hud.GubbleViewOpen && eye.Live && viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Always && view.Visible &&
                  _hud.GubbleViewPicture.Texture?.GetInstanceId() == viewport.GetTexture().GetInstanceId() && !player.InputEnabled && Input.MouseMode == Input.MouseModeEnum.Visible,
                "I opens the view through the Gubble's eyes: its eye draws every frame onto the whole screen, and the body waits");
            Check(camera.CullMask == EyeView.GubbleEyeCullMask && companion.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().All(g => g.Layers == EyeView.GubbleBodyLayer),
                "with the Gubble's own cull mask: every drawn part of the Gubble is on its layer, which its eye leaves out");
            Check(GetViewport().Disable3D && _hud.Main3DOffForView, "the window's own 3D waits while the view covers the screen (the world is drawn once a frame)");
            Check(_hud.GubbleViewTitle == "Through the Gubble's eyes", "a plain label says whose eyes these are: " + _hud.GubbleViewTitle);
            Check(camera.GlobalPosition.DistanceTo(companion.EyeCamera.GlobalPosition) < 1e-3f && (-camera.GlobalBasis.Z).Dot(-companion.GlobalBasis.Z) > 0.9999f &&
                  Mathf.IsEqualApprox(camera.Fov, companion.EyeCamera.Fov),
                "the camera is at the Gubble's eye, looking the way the Gubble faces, with its lens");
            camera.Transform = Transform3D.Identity;
            await Frames(1);
            Check(camera.GlobalPosition.DistanceTo(companion.EyeCamera.GlobalPosition) < 1e-3f, "it is placed again every frame (it follows the Gubble live)");

            await NothingReachesTheGameThroughTheEyes();
            await TestGubbleEyeDrag(camera);

            // Esc returns: the eye goes idle, the body moves again, the window draws its own 3D again.
            _hud._UnhandledInput(Press(Key.Escape));
            Check(!_hud.GubbleViewOpen && !eye.Live && viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled && !view.Visible && player.InputEnabled &&
                  !GetViewport().Disable3D && !_hud.Main3DOffForView,
                "Esc returns to the player's own eyes: the Gubble's eye is idle again and the body moves");
            var after = WorldState(host);
            Check(after == room, "looking through the Gubble's eyes, turning its eye and coming back changed no world state (no command, no receipt, nothing moved)" + Changed(room, after));

            // The icon: a real click opens and a second returns; the picture keeps clicks from the journal's book under it.
            await ClickThroughViewport(icon);
            Check(_hud.GubbleViewOpen && icon.Looking && icon.TooltipText == "Back to your own eyes · I", "a real mouse click on the eye icon opens the view, and the icon offers the way back");
            await ClickThroughViewport(_hud.Book.Dock);
            Check(_hud.GubbleViewOpen && !_hud.JournalOpen, "a click where the journal's book sits does not open the book under the view");
            _hud._UnhandledInput(Press(Key.J));
            Check(_hud.GubbleViewOpen && !_hud.JournalOpen, "nor does J: the view holds the keys");
            await ClickThroughViewport(icon);
            Check(!_hud.GubbleViewOpen && !icon.Looking && player.InputEnabled, "a second click on the icon returns");
            _hud._UnhandledInput(Press(Key.I));
            _hud._UnhandledInput(Press(Key.I));
            Check(!_hud.GubbleViewOpen && player.InputEnabled, "and I again returns too");
            var owner = GetViewport().GuiGetFocusOwner();
            Check(owner != icon, $"after its clicks the icon holds no keyboard focus ({owner?.Name})");
            foreach (var key in new[] { Key.Space, Key.Enter })
            {
                GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }, true);
                GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }, true);
                await Frames(2);
                Check(!_hud.GubbleViewOpen, $"{key} never presses the icon");
            }

            // From the appearance panel, I opens the view and closes the panel; the journal holds I, and the icon rests while the book is out.
            _hud._UnhandledInput(Press(Key.C));
            _hud._UnhandledInput(Press(Key.I));
            Check(_hud.GubbleViewOpen && !_hud.SettingsOpen && !player.InputEnabled, "I from the appearance panel closes it and opens the view");
            _hud._UnhandledInput(Press(Key.I));
            _hud._UnhandledInput(Press(Key.J));
            _hud._UnhandledInput(Press(Key.I));
            await Frames(2);
            Check(_hud.JournalOpen && !_hud.GubbleViewOpen && !icon.Visible, "while the journal is open I waits, and the icon rests until the book is back in its dock");
            _hud._UnhandledInput(Press(Key.Escape));
            await Frames(JournalMoveFrames);
            Check(!_hud.JournalOpen && icon.Visible && player.InputEnabled, "with the book back in its dock the icon is there again");

            await TestGubbleWordsOverView();
            await TestAbilitiesNameTheView();
            await TestGubbleLookCue(host, icon);
            await TestGubbleSeesPlayerInF1();
        }
        finally
        {
            _hud.CloseGubbleView();
            _hud.DragHeldForTests = null;
            host.LookCaptureForTests = null;
            host.SetGubbleEyes(true);
            _hud.SetViewMode(1);
        }
    }

    /// <summary>
    /// What a view must never change: the host's revision, the room's entities (their revisions, poses and locks), receipts,
    /// effects, the journal's notes and tasks, orders, the bodies, what they hold and the profile. (The export's session id and
    /// the weather's clock change on their own.)
    /// </summary>
    private string WorldState(CommandHost host)
    {
        var companion = _world.Companion;
        var profile = Godot.FileAccess.FileExists(RoomHud.ProfilePath) ? Godot.FileAccess.GetFileAsString(RoomHud.ProfilePath) : "";
        // The floating Gubble bobs about 4 mm on its own; its height rounded to the centimetre could cross a boundary between
        // two reads, so only where it is across the ground counts as world state here (K6's finding, the characters round).
        static string Round(Vector3 v) => $"{v.X:0.00},{v.Y:0.00},{v.Z:0.00}";
        var state = host.ExportRoomState();
        return string.Join("\n", host.Revision, state?["entities"]?.ToJsonString() ?? "", state?["journal"]?.ToJsonString() ?? "", host.TransientReceiptCount(CommandHost.PlayerPrincipal),
            host.TransientReceiptCount(CommandHost.CompanionPrincipal), string.Join(",", host.ActiveEffectIds), companion.CurrentIntent,
            Round(_world.Player.GlobalPosition), Round(companion.GlobalPosition with { Y = 0 }), Round(-companion.GlobalBasis.Z), host.HeldBy(CommandHost.PlayerAvatar) ?? "",
            host.HeldBy(CommandHost.CompanionAvatarId) ?? "", host.GubbleEyesAllowed, profile);
    }

    /// <summary>The parts of two world states that differ, to say what changed when a check fails.</summary>
    private static string Changed(string before, string after)
    {
        var (a, b) = (before.Split('\n'), after.Split('\n'));
        var changed = Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(i => a[i] != b[i]).Select(i => $"part {i}: {a[i]} -> {b[i]}").ToArray();
        return changed.Length == 0 ? "" : " (" + string.Join("; ", changed) + ")";
    }

    /// <summary>Every gameplay key, the Gubble's ask and wheel, the zoom and J, handed to the HUD while the view is open: none gets through.</summary>
    private async Task NothingReachesTheGameThroughTheEyes()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        var effects = host.ActiveEffectIds.Count;
        var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
        var intent = companion.CurrentIntent;
        var help = _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible;
        var (view, physics, lamps, observe) = (_hud.ViewMode, _world.Player.WorldPhysicsId, _world.Look.LampsOn, _world.Look.Observe);
        foreach (var key in new[] { Key.Key1, Key.Key3, Key.Q, Key.X, Key.F, Key.V, Key.H, Key.F1, Key.F3, Key.C, Key.T, Key.G, Key.L, Key.O, Key.B, Key.R, Key.J })
            _hud._UnhandledInput(Press(key));
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var wheel = _hud.Wheel.Held || _hud.Wheel.Open || GetTree().Paused;
        _hud._UnhandledInput(RightButton(false));
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        Check(host.ActiveEffectIds.Count == effects && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && companion.CurrentIntent == intent &&
              _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible == help &&
              _hud.ViewMode == view && _world.Player.WorldPhysicsId == physics && _world.Look.LampsOn == lamps && _world.Look.Observe == observe &&
              !_hud.SettingsOpen && !_hud.JournalOpen && _hud.TimeStop == -1 && !wheel && !_world.Player.InputEnabled && _hud.GubbleViewOpen,
            "through the Gubble's eyes, no gameplay key, no order to the Gubble, no magic, no ask, no wheel, no panel and no journal gets through; the body waits");
    }

    /// <summary>A drag turns and tilts the Gubble's eye, never its body or the player's, and stops at the AI's bounds (90 and 60 degrees).</summary>
    private async Task TestGubbleEyeDrag(Camera3D camera)
    {
        var companion = _world.Companion;
        var player = _world.Player;
        _hud.DragHeldForTests = () => true;
        var body = companion.GlobalBasis;
        var (playerYaw, playerPitch) = (player.GlobalRotation.Y, player.EyeCamera.Rotation.X);
        var facing = -companion.GlobalBasis.Z;
        _hud._UnhandledInput(LeftButton(true));
        _hud._UnhandledInput(Motion(new Vector2(40, -20)));
        var expectedYaw = Mathf.RadToDeg(40 * RoomHud.MouseRadiansPerPixel);
        var expectedPitch = Mathf.RadToDeg(20 * RoomHud.MouseRadiansPerPixel);
        Check(_hud.Drag.Dragging && Mathf.Abs(_hud.GubbleViewYawDeg - expectedYaw) < 1e-3f && Mathf.Abs(_hud.GubbleViewPitchDeg - expectedPitch) < 1e-3f,
            $"a drag right and up turns the Gubble's eye right and tilts it up, as a drag turns the player's ({_hud.GubbleViewYawDeg:0.00} and {_hud.GubbleViewPitchDeg:0.00} degrees)");
        Check(companion.GlobalBasis.IsEqualApprox(body) && Mathf.IsEqualApprox(playerYaw, player.GlobalRotation.Y) && Mathf.IsEqualApprox(playerPitch, player.EyeCamera.Rotation.X),
            "the Gubble's body does not turn, nor does the player's");
        _hud._UnhandledInput(Motion(new Vector2(100000, -100000)));
        var forward = -camera.GlobalBasis.Z;
        var turned = Mathf.RadToDeg(new Vector2(facing.X, facing.Z).AngleTo(new Vector2(forward.X, forward.Z)));
        Check(_hud.GubbleViewYawDeg == (float)CommandHost.LookMaxYawDeg && _hud.GubbleViewPitchDeg == (float)CommandHost.LookMaxPitchDeg &&
              Mathf.Abs(turned - 90) < 0.1f && Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(forward.Y)) - 60) < 0.1f,
            $"it turns at most {CommandHost.LookMaxYawDeg} degrees to the right and tilts at most {CommandHost.LookMaxPitchDeg} up, the AI's own bounds (the camera turned {turned:0.0})");
        _hud._UnhandledInput(Motion(new Vector2(-300000, 300000)));
        forward = -camera.GlobalBasis.Z;
        turned = Mathf.RadToDeg(new Vector2(facing.X, facing.Z).AngleTo(new Vector2(forward.X, forward.Z)));
        Check(_hud.GubbleViewYawDeg == -(float)CommandHost.LookMaxYawDeg && _hud.GubbleViewPitchDeg == -(float)CommandHost.LookMaxPitchDeg &&
              Mathf.Abs(turned + 90) < 0.1f && Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(forward.Y)) + 60) < 0.1f,
            "and as far to the left and down");
        _hud._UnhandledInput(LeftButton(false));
        Check(!_hud.Drag.Pressed && companion.GlobalBasis.IsEqualApprox(body), "letting go ends the drag; the Gubble still faces where it did");
        _hud.DragHeldForTests = null;
        await Frames(1);
    }

    /// <summary>
    /// The Gubble's words show while the player looks through its eyes (the founder: "It's still information the player needs to
    /// be aware of"): its bubble's answer and reason come to the top of the view, above the view's label, over the picture, as large
    /// and outlined as the bubble's own, and go when the bubble clears.
    /// </summary>
    private async Task TestGubbleWordsOverView()
    {
        var cue = _hud.Cue;
        var top = _hud.GetNode<Control>("GubbleView/GubbleViewTop");
        var bubble = top.GetNode<Label>("GubbleViewBubble");
        var says = top.GetNode<Label>("GubbleViewSays");
        var label = top.GetNode<Control>("GubbleViewLabel");
        cue.Think(GubbleMagic.FetchIcon);
        _hud.OpenGubbleView();
        Check(_hud.GubbleViewBubbleText == cue.Bubble.Text && _hud.GubbleViewBubbleText.Length > 0 && _hud.GubbleViewSaysText == "",
            "an answer the Gubble is thinking about shows over its view at once: " + _hud.GubbleViewBubbleText);
        cue.Refuse("That spot is out of reach. Bring the Gubble closer and try again.");
        await Frames(2);
        var screen = _hud.GetViewport().GetVisibleRect();
        var (b, s, l) = (bubble.GetGlobalRect(), says.GetGlobalRect(), label.GetGlobalRect());
        Check(_hud.GubbleViewBubbleText == cue.Bubble.Text && _hud.GubbleViewSaysText == cue.Reason && cue.Reason.Length > 0,
            $"a refusal while looking shows the host's reason there too: {_hud.GubbleViewBubbleText} {_hud.GubbleViewSaysText}");
        Check(b.End.Y <= s.Position.Y + 0.5f && s.End.Y <= l.Position.Y + 0.5f && screen.Encloses(b) && screen.Encloses(s) &&
              Mathf.Abs(b.GetCenter().X - screen.GetCenter().X) < 1 && Mathf.Abs(s.GetCenter().X - screen.GetCenter().X) < 1,
            $"the words stand centred at the top, above the view's label (bubble {b}, words {s}, label {l})");
        Check(top.GetIndex() > _hud.GubbleViewPicture.GetIndex() && bubble.GetThemeFontSize("font_size") == GubbleCue.BubbleFontPx &&
              says.GetThemeFontSize("font_size") == GubbleCue.ReasonFontPx && says.GetThemeConstant("outline_size") == GubbleCue.OutlinePx,
            "over the picture, as large as the bubble's own words and outlined dark, so they read over any view");
        cue.Clear();
        await Frames(2);
        Check(!bubble.Visible && !says.Visible && Mathf.Abs(label.GetGlobalRect().Position.Y - 18) < 1, "when the bubble clears, the words go and the label is back at the top");
        _hud.CloseGubbleView();
    }

    /// <summary>The journal's Abilities page names the view, with its key from the input map and the icon, in a player's words.</summary>
    private async Task TestAbilitiesNameTheView()
    {
        _hud.OpenJournal();
        _hud.Journal.OpenPage(JournalPages.Abilities);
        Check(_hud.JournalText.Contains("\nI or the eye icon: see through the Gubble's eyes\n", StringComparison.Ordinal),
            "the journal's Abilities page has a line for seeing through the Gubble's eyes: its key or the eye icon");
        // The pages still fit: every line keeps within the page's width (a long page scrolls, as it did), and the new line is short.
        await Frames(JournalMoveFrames);
        foreach (var page in new[] { JournalPages.Discoveries, JournalPages.Abilities })
        {
            _hud.Journal.OpenPage(page);
            await Frames(3);
            var scroll = _hud.Journal.FindChildren("*", nameof(ScrollContainer), true, false).OfType<ScrollContainer>().First();
            var area = scroll.GetGlobalRect();
            var labels = scroll.GetChild<Control>(0).FindChildren("*", nameof(Label), true, false).OfType<Label>().ToList();
            var eyes = labels.FirstOrDefault(l => l.Text.Contains("eye icon", StringComparison.Ordinal));
            GD.Print($"PLAY_HUD_INFO: the journal's {page} page: {labels.Count} lines, {scroll.GetChild<Control>(0).Size.Y:0} px of words in a {area.Size.Y:0} px page");
            Check(labels.Count > 0 && labels.All(l => l.GetGlobalRect().Position.X >= area.Position.X - 1 && l.GetGlobalRect().End.X <= area.End.X + 1) &&
                  (page != JournalPages.Abilities || eyes is { } line && line.GetLineCount() <= 2),
                $"the {page} page's lines keep within the page's width" + (page == JournalPages.Abilities ? $", the eyes line on {eyes?.GetLineCount()} line(s)" : ""));
        }
        _hud.Journal.OpenPage(JournalPages.Abilities);
        SaveMap();
        try
        {
            Bind(Act.GubbleEyes, Key.U);
            _hud.Journal.OpenPage(JournalPages.Abilities);
            Check(_hud.JournalText.Contains("U or the eye icon: see through the Gubble's eyes", StringComparison.Ordinal), "with its key read from the input map (on U, it says U)");
        }
        finally { RestoreMap(); }
        _hud.CloseJournal();
        await Frames(JournalMoveFrames);
    }
    /// <summary>The icon blinks when the player's AI takes a picture (view.look succeeds), and not when the look is refused or not drawn.</summary>
    private async Task TestGubbleLookCue(CommandHost host, GubbleEyesIcon icon)
    {
        var plain = Image.CreateEmpty(CommandHost.LookWidthPx, CommandHost.LookHeightPx, false, Image.Format.Rgb8);
        plain.Fill(new Color(0.4f, 0.6f, 0.8f));
        var picture = EyeView.Encode(plain, CommandHost.LookImageMaxBytes);
        host.LookCaptureForTests = _ => Task.FromResult(picture);
        var look = 0;
        Task<JsonObject> Look(string principal) => host.HandleObjectAsync(new JsonObject
        {
            ["schema"] = "enfractal.query", ["version"] = 1, ["query_id"] = $"hud-eyes-cue-{++look}", ["room_id"] = _world.Room.RoomId, ["op"] = "view.look",
            ["args"] = new JsonObject { ["actor"] = CommandHost.CompanionAvatarId },
        }.ToJsonString(), principal);
        await Frames(50);
        var companionParts = _world.Companion.GetChildCount();
        var cue = (_hud.Cue.Icon, _hud.Cue.State, _hud.Cue.Reason, _hud.Cue.Bubble.Visible);
        var blinks = icon.Blinks;
        Check(!icon.Blinking, "(the icon is still before the AI looks)");

        var taken = await Look(CommandHost.CompanionPrincipal);
        Check(taken["ok"]?.GetValue<bool>() == true && taken["data"]?["image"]?["media_type"]?.GetValue<string>() == "image/jpeg" && icon.Blinks == blinks + 1 && icon.Blinking,
            "when the player's AI takes a picture through the Gubble's eyes, the eye icon blinks");
        await Frames(4);
        Check(icon.LidClosed > 0 && _world.Companion.GetChildCount() == companionParts && (_hud.Cue.Icon, _hud.Cue.State, _hud.Cue.Reason, _hud.Cue.Bubble.Visible) == cue,
            "a soft blink of its lid on the icon; nothing appears on the Gubble or in its thought bubble");
        await Frames((int)Math.Ceiling(GubbleEyesIcon.BlinkSeconds * 60) + 4);
        Check(!icon.Blinking && icon.LidClosed == 0 && icon.Blinks == blinks + 1, $"and it is short: one blink of {GubbleEyesIcon.BlinkSeconds} s, then the eye is open again");

        host.SetGubbleEyes(false);
        var refused = await Look(CommandHost.CompanionPrincipal);
        host.SetGubbleEyes(true);
        Check(refused["error"]?["code"]?.GetValue<string>() == "permission_denied" && icon.Blinks == blinks + 1 && !icon.Blinking,
            "a look the player's setting refuses does not blink");
        host.LookCaptureForTests = _ => Task.FromResult<EyePicture?>(null);
        var notDrawn = await Look(CommandHost.CompanionPrincipal);
        Check(notDrawn["error"]?["code"]?.GetValue<string>() == "unavailable" && icon.Blinks == blinks + 1, "nor does a look whose picture was not drawn");
        host.LookCaptureForTests = _ => Task.FromResult(picture);
        var players = await Look(CommandHost.PlayerPrincipal);
        Check(players["ok"]?.GetValue<bool>() == true && icon.Blinks == blinks + 1, "nor the player's own look: the cue is for the AI's");
        host.LookCaptureForTests = null;
    }

    /// <summary>
    /// The F1 gap from the eyes work, closed: in the first-person view the player's body stays drawn on its own layer, which only
    /// the player's own eye leaves out, so the Gubble's eye (the AI's picture, the player's view through it) always sees the player.
    /// </summary>
    private async Task TestGubbleSeesPlayerInF1()
    {
        var player = _world.Player;
        var companion = _world.Companion;
        _hud.SetViewMode(0);
        // Physics ticks pass: the old rule hid the body for every camera on each tick while the eye camera was current.
        await Frames(6);
        var body = player.GetNode<Node3D>("OriginalPrototypeBody");
        var meshes = body.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
        Check(player.EyeCamera.Current && meshes.Count > 0 && meshes.All(m => m.IsVisibleInTree() && m.Layers == EyeView.PlayerBodyLayer),
            $"in F1 the player's body is still drawn, every part on the player's body layer ({meshes.Count} parts)");
        Check(player.EyeCamera.CullMask == EyeView.PlayerEyeCullMask && (player.EyeCamera.CullMask & EyeView.PlayerBodyLayer) == 0 && (player.EyeCamera.CullMask & EyeView.GubbleBodyLayer) != 0,
            "the player's own eye leaves that layer out, so F1 shows no body as before, and still draws the Gubble");
        _hud.OpenGubbleView();
        Check(_hud.GubbleViewOpen && (_hud.GubbleEye.EyeCamera!.CullMask & EyeView.PlayerBodyLayer) != 0 && (EyeView.GubbleEyeCullMask & EyeView.PlayerBodyLayer) != 0,
            "the Gubble's eye (the player's view through it, and the AI's picture) draws the player's body in F1");
        _hud.CloseGubbleView();
        var follow = (Camera3D)player.FindChild("FollowCamera", true, false)!;
        Check((follow.CullMask & EyeView.PlayerBodyLayer) != 0 && (_hud.DioramaCamera.CullMask & EyeView.PlayerBodyLayer) != 0,
            "the shoulder, diorama and isometric cameras still draw the player's body");
        Check(companion.EyeCamera.CullMask == EyeView.GubbleEyeCullMask, "and the Gubble's own eye camera leaves out only the Gubble's body");
        _hud.SetViewMode(1);
        await Frames(2);
    }
}
