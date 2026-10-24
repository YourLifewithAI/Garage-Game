using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Glow journal in the HUD: fresh, it claims no discovery and spoils none; learned, it offers the cast it teaches,
/// plays its chime and stops offering once the discovery is made.
/// </summary>
public partial class PlayHudTest
{
    private const string HudGlowProfile = "user://tests/play_hud/profile/glow_progress_v1.json";
    private ulong _lastGlowPlaybackId;
    private ulong _lastGlowVoiceId;

    private static bool HudAudioResourceAlive(ulong id)
    {
        // IsInstanceIdValid creates a managed wrapper too, but does not dispose
        // it. Release this observation's reference so it cannot retain the sound.
        using var resource = InstanceFromId(id);
        return resource != null;
    }

    /// <summary>
    /// The Glow journal's protections, now on the one journal's Discoveries page (the separate Glow journal panel is retired):
    /// reading holds movement while the island goes on, the page names no undiscovered ability and no trigger, and no key or
    /// mouse press reaches the game through the open book.
    /// </summary>
    private async Task TestGlowJournalFresh()
    {
        var host = CommandHost.Of(_world)!;
        var dock = _hud.Book.Dock;
        Check(!_hud.JournalOpen && dock.Visible && !host.GlowProgress.HasDiscovery("lantern") && _hud.FindChild("GlowJournalButton", true, false) == null,
            "a fresh trial starts with the journal in its dock, no discovery claimed, and no separate Glow journal");
        dock.EmitSignal(Button.SignalName.Pressed);
        await Frames(1);
        Check(_hud.JournalOpen && !_world.Player.InputEnabled && !GetTree().Paused,
            "a click on the book opens the journal and holds player movement while the island continues");
        _hud.Journal.OpenPage(JournalPages.Discoveries);
        var text = _hud.JournalText;
        Check(text.Contains("3 lights", StringComparison.Ordinal) &&
              text.Contains("Your Glow expertise will appear here.", StringComparison.Ordinal) &&
              !text.Contains("Lantern", StringComparison.Ordinal) && NoGlowSpoilers(text),
            "the Discoveries page shows the current light allowance without naming undiscovered abilities or their unlock conditions");
        Check(GlowSmudges() == 5 && _hud.Journal.Lines.Count(line => line.Ink == JournalInk.Smudge && line.Text == JournalPages.Smudge) == 15,
            "each Glow discovery still to come is the same unreadable smudge (and each of Fire's and Grow's), so there is more to find but nothing says what");
        var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
        _hud._UnhandledInput(Press(Key.Key1));
        Check(host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && host.ActiveEffectIds.Count == 0,
            "reading the journal does not cast a light when an ability key is pressed");
        var previousView = _hud.ViewMode;
        _hud.SetViewMode(1);
        _hud.ZoomShoulder(-4);
        var distance = _hud.ShoulderDistanceM;
        _hud._UnhandledInput(LeftButton(true));
        _hud._UnhandledInput(new InputEventMouseMotion { Relative = new Vector2(50, 20) });
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        _hud._UnhandledInput(RightButton(true));
        Check(!_hud.Drag.Pressed && !_hud.Wheel.Held && _hud.ShoulderDistanceM == distance,
            "mouse presses over the open journal cannot begin a look drag, zoom or open the magic wheel");
        _hud.ZoomShoulder(1000);
        _hud.SetViewMode(previousView);
        _hud._UnhandledInput(Press(Key.C));
        Check(_hud.JournalOpen && !_hud.SettingsOpen && !_world.Player.InputEnabled,
            "C waits while the book is open: the book keeps the keyboard, all but its own key and Esc");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.JournalOpen && !_hud.Book.WantsOpen && !_world.Player.InputEnabled, "Esc closes the book, and movement stays held while it shrinks back");
        await Frames(60);
        Check(!_hud.JournalOpen && _world.Player.InputEnabled, "back in its dock, the book returns player control");
        _hud._UnhandledInput(Press(Key.C));
        _hud._UnhandledInput(Press(Key.J));
        Check(_hud.JournalOpen && !_hud.SettingsOpen && !_world.Player.InputEnabled,
            "opening the journal from the settings leaves only the journal open");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(60);
        Check(!_hud.JournalOpen && !_hud.SettingsOpen && _world.Player.InputEnabled,
            "the existing Escape action closes the journal and restores player control");
    }

    private async Task TestGlowJournalLearned()
    {
        var host = CommandHost.Of(_world)!;
        _hud._UnhandledInput(Press(Key.X));
        // Seed only validated host metadata to exercise the persistent hint's presentation. A command cannot supply it.
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Restore(10, Array.Empty<string>(), new[] { "lantern_self" })));
        _hud.OpenJournal();
        _hud.Journal.OpenPage(JournalPages.Discoveries);
        await Frames(1);
        Check(_hud.JournalText.Contains("Try asking me to glow on myself.", StringComparison.Ordinal) && NoGlowSpoilers(_hud.JournalText),
            "a restored hint uses its curated words and suggests only the implemented self Glow");
        var cast = host.PlayerEffect("glow");
        await Frames(1);
        var chime = _world.Look.GlowRoot(host.ActiveEffectIds[0])!.GetNode<AudioStreamPlayer3D>("GlowChime");
        Check(chime.Playing && chime.HasStreamPlayback(), "the learned journal cast starts a real chime before its teardown check");
        _lastGlowVoiceId = chime.Stream.GetInstanceId();
        using (var playback = chime.GetStreamPlayback()) _lastGlowPlaybackId = playback.GetInstanceId();
        // The open page is read again from the host while the book is open (JournalPanel.RefreshSeconds).
        await Frames(JournalRefreshFrames);
        Check(cast["ok"]!.GetValue<bool>() && host.GlowProgress.HasDiscovery("lantern") &&
              _hud.JournalText.Contains("Lantern\nA light that travels with the Gubble.", StringComparison.Ordinal),
            "a successful host cast records Lantern and the open journal shows its verified discovery");
        Check(!_hud.JournalText.Contains("Try asking me", StringComparison.Ordinal) && NoGlowSpoilers(_hud.JournalText) &&
              GlowSmudges() == 4,
            "the learned page retires the now-completed hint and one smudge, without revealing future discoveries");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(60);
        _hud._UnhandledInput(Press(Key.X));
        host.ConfigureGlowProgress(new LocalGlowProgressStore(ProjectSettings.GlobalizePath(HudGlowProfile)));
    }

    /// <summary>
    /// What the pair learned in Bubbles and Fire is listed by its plain name (M11), and nothing more while their lists are not final:
    /// no words, no smudge for what is to come, no hint, nothing at all for an ability with nothing learned.
    /// </summary>
    private async Task TestOtherExpertiseJournal()
    {
        var host = CommandHost.Of(_world)!;
        _hud.OpenJournal();
        _hud.Journal.OpenPage(JournalPages.Discoveries);
        await Frames(1);
        var fresh = _hud.JournalText;
        var smudges = _hud.Journal.Lines.Count(line => line.Ink == JournalInk.Smudge);
        var fireSmudges = ExpertiseLists.FireOrder.Length - (host.GlowProgress.Of(ExpertiseLists.Fire)?.Discoveries.Count ?? 0);
        Check(!fresh.Contains("Bubbles", StringComparison.Ordinal) && fresh.Contains("Fire\n" + JournalPages.Smudge, StringComparison.Ordinal) &&
              !fresh.Contains("Campfire", StringComparison.Ordinal) && !fresh.Contains("Snuff", StringComparison.Ordinal),
            "with nothing learned, the Discoveries page says nothing of Bubbles, and Fire's final list (decisions 77 to 80) shows only as smudges");
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Restore(11, new[] { "lantern" }, new[] { "lantern_self" }, "plain", 0.35, false, new[]
        {
            ExpertiseRecord.Restore(ExpertiseLists.BubblesList, 0, new[] { ExpertiseLists.BubbleCarry, ExpertiseLists.BubbleLift }, Array.Empty<string>()),
            ExpertiseRecord.Restore(ExpertiseLists.FireList, 0, new[] { ExpertiseLists.FirstFire, ExpertiseLists.WillOWisp }, Array.Empty<string>()),
        })));
        _hud.Journal.OpenPage(JournalPages.Discoveries);
        await Frames(JournalRefreshFrames);
        var text = _hud.JournalText;
        Check(text.Contains("Bubbles\nBubble carry\nBubble lift", StringComparison.Ordinal) && text.Contains("Fire\nFire\nWill-o'-wisp", StringComparison.Ordinal) &&
              !text.Contains("Bubble boat", StringComparison.Ordinal) && !text.Contains("Campfire", StringComparison.Ordinal) && !text.Contains("Fireline", StringComparison.Ordinal) &&
              !text.Contains("Snuff", StringComparison.Ordinal),
            "a learned expertise is listed by its plain name under its ability, and one not learned is not named");
        Check(_hud.Journal.Lines.Count(line => line.Ink == JournalInk.Smudge) == smudges - fireSmudges + 3 && _hud.Journal.Lines.Count(line => line.Text == "A thought from the Gubble") == 0 &&
              NoGlowSpoilers(text),
            "and Bubbles adds no smudge and no hint, while Fire's two learned entries take two of its smudges' places (no thought from the Gubble: none delivered)");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(60);
        host.ConfigureGlowProgress(new LocalGlowProgressStore(ProjectSettings.GlobalizePath(HudGlowProfile)));
    }

    private static bool NoGlowSpoilers(string text) =>
        !text.Contains("Torch", StringComparison.Ordinal) && !text.Contains("Bioluminesce", StringComparison.Ordinal) &&
        !text.Contains("Constellation", StringComparison.Ordinal) && !text.Contains("Skylight", StringComparison.Ordinal) &&
        !text.Contains("casts", StringComparison.OrdinalIgnoreCase) && !text.Contains("XP", StringComparison.Ordinal);

    private sealed class HudGlowStore(GlowProgress state) : IGlowProgressStore
    {
        public GlowProgress Load() => state;
        public void Save(GlowProgress next) => state = next;
    }

    /// <summary>The journal page's smudges in Glow's part (before Grow's list, decision 75, whose own smudges follow).</summary>
    private int GlowSmudges() => _hud.Journal.Lines.TakeWhile(line => !(line.Ink == JournalInk.Title && line.Text is "Bubbles" or "Fire" or "Grow")).Count(line => line.Ink == JournalInk.Smudge);
}
