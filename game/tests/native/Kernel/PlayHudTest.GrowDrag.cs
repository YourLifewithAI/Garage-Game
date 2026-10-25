using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Grow's drag and locks on screen (MECHANICS.md decisions 76 and 104 to 107; docs/magic/GROW-UNLOCKS.md; RoomHud.GrowDrag.cs): hold 2 and sweep the
/// aim from lip to lip for a bridge (the host's preview, then the cast of that line, the outline flashing), a tap as before; before three of Grow's
/// expertise the drag sends nothing and says the host's lock words; the wheel's Grow wedge, the Magic panel and the journal name what is locked
/// and what is found, by the host's own count.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>The Grow casts the host handled (a preview or a cast, with its drag's end if it had one), in order.</summary>
    private readonly List<(bool Preview, bool Ok, string? Message, Vector3? End)> _growCasts = new();

    private void ListenForGrow(CommandHandled handled)
    {
        if (handled.Op != "nature.cast") return;
        var envelope = JsonNode.Parse(handled.Message)?.AsObject();
        if (envelope?["args"]?["capability"]?.GetValue<string>() != "grow") return;
        Vector3? end = envelope["args"]?["end_m"] is JsonArray { Count: 3 } xyz ? new Vector3(xyz[0]!.GetValue<float>(), xyz[1]!.GetValue<float>(), xyz[2]!.GetValue<float>()) : null;
        _growCasts.Add((handled.Preview, handled.Ok, handled.ErrorMessage, end));
    }

    /// <summary>The aim's floor point at a spot, or null (nothing hit, or the Gubble).</summary>
    private Vector3? GrowFloor(Vector3 spot)
    {
        _hud.AimRayForTests = Down(spot);
        var aim = _hud.AimNow();
        return aim.Hit && !aim.AtGubble ? aim.Point : null;
    }

    private void LearnGrow(CommandHost host, int count)
    {
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Fresh));
        host.LearnGrowForTests(count);
    }

    private async Task TestGrowDragAndLocks()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        host.Handled += ListenForGrow;
        var previousSample = host.NatureSampleForTests;
        var look = _world.GetNodeOrNull<NatureLook>("NatureLook");
        try
        {
            host.Nature.WeatherS = 0;
            host.NatureSampleForTests = (x, z) => new NatureCell { X = x, Z = z, Y = 0, Material = "soil", Fertile = true, Fuel = 1, Moisture = .2f, Light = .6f };
            host.PlayerGoal("follow");
            await Frames(10);
            _hud.LightForTests = _ => 0.9f;
            var a = FloorNearGubble(.3f);
            Vector3? b = null;
            for (var i = 0; i < 8 && b == null; i++)
            {
                var spot = a + new Vector3(Mathf.Sin(i * Mathf.Pi / 4), 0, Mathf.Cos(i * Mathf.Pi / 4)) * 0.6f;
                if (GrowFloor(spot) is { } hit && Mathf.Abs(hit.Y - a.Y) < 0.005f && host.Room.Bounds.HasPoint(hit + Vector3.Up * 0.001f)) b = hit;
            }
            Check(b != null, "(a second floor spot 0.6 m from the first, for a drag)");
            if (b == null) return;
            var lip = b.Value;
            var along = (lip - a).Normalized();

            // ---- before the bridge: three of Grow's expertise open it ----
            LearnGrow(host, 0);
            await Frames(2);
            Check(!host.BridgeUnlocked && !host.SeedlessUnlocked && host.GrowLockedKinds().Count == 4, "(no Grow expertise learned: all four unlocks locked)");
            _hud.AimRayForTests = Down(FloorNearGubble(.3f));
            _hud._UnhandledInput(RightButton(true));
            await Frames(20);
            var labels = _hud.Wheel.WedgeLabels.Select(l => l.Text).ToArray();
            Check(labels[4] == "✿\n2 Grow\nlearn 2 more\nfor sprouts" && _hud.Wheel.NotedWedges.SequenceEqual(new[] { 4 }) && !_hud.Wheel.LockedWedges.Contains(4) && _hud.Wheel.WedgeLabels[4].Modulate.A == 1f,
                "the wheel's Grow wedge stays bright and carries a padlock and a note on what is next: " + labels[4].Replace("\n", " "));
            _hud._UnhandledInput(Motion(new Vector2(0, 0)));
            _hud._UnhandledInput(RightButton(false));
            await Frames(2);
            var panelLocked = string.Join("\n", GrowLocks.All.Select(u => "Locked: " + host.GrowLockedWords(u.Kind)));
            _hud.ToggleMagicPanel();
            await Frames(14);
            Check(_hud.GrowUnlocksText == panelLocked && _hud.GrowUnlocksText.Contains(CommandHost.BridgeLocked, StringComparison.Ordinal) && _hud.GrowUnlocksText.Contains(CommandHost.IvyLocked, StringComparison.Ordinal),
                "the Magic panel's Grow section says each unlock is locked in the host's own words: " + _hud.GrowUnlocksText.Replace("\n", " | "));
            _hud.ToggleMagicPanel();
            Check(!HelpLines().Any(l => l.Contains("sweep the aim from lip to lip", StringComparison.Ordinal)), "the help does not teach the drag before it is open");

            // A drag now sends nothing: the host's lock words are the answer (as 6 says its own before the first rest).
            var cast = _growCasts.Count;
            var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
            var revision = host.Revision;
            _hud.AimRayForTests = Down(a);
            _hud._UnhandledInput(Press(Key.Key2));
            var held = _hud.GrowKeyHeld;
            _hud.AimRayForTests = Down(lip);
            _hud._UnhandledInput(Release(Key.Key2));
            await Frames(2);
            Check(held && !_hud.GrowKeyHeld && _growCasts.Count == cast && host.Revision == revision && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts &&
                  _hud.NoticeText == CommandHost.BridgeLocked && _hud.Cue.State == "shrug" && _hud.Cue.Reason == CommandHost.BridgeLocked,
                "holding 2 and sweeping the aim before the bridge is open sends no command and says the host's words: " + _hud.NoticeText);
            // A tap is Grow at the press's spot, as it always was; the place decides, and its refusal is shown as the host words it.
            _hud.AimRayForTests = Down(a);
            _hud._UnhandledInput(Press(Key.Key2));
            _hud._UnhandledInput(Release(Key.Key2));
            await Frames(2);
            Check(_growCasts.Count == cast + 1 && _growCasts[^1].End == null && !_growCasts[^1].Preview && !_growCasts[^1].Ok && _growCasts[^1].Message == CommandHost.SproutsLocked && _hud.NoticeText == CommandHost.SproutsLocked,
                "a tap casts Grow at the spot with no end, and on bare soil with sprouts locked the host's words are shown: " + _hud.NoticeText);

            // The Magic panel's Grow button says the same words (it used to cover a refusal of this kind with a general one).
            _hud.AimRayForTests = Down(a);
            _hud.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "Choose spot").EmitSignal(Button.SignalName.Pressed);
            _hud._UnhandledInput(LeftButton(true));
            await Frames(2);
            _hud.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "Grow the plants at the selected spot").EmitSignal(Button.SignalName.Pressed);
            Check(_hud.MagicPanelOpen && _hud.MagicMessage == CommandHost.SproutsLocked, "and so does the Magic panel's Grow button, in the host's words: " + _hud.MagicMessage);
            _hud.ToggleMagicPanel();

            // ---- two expertise: sprouts and replanting ----
            LearnGrow(host, 2);
            _hud.AimRayForTests = Down(FloorNearGubble(.3f));
            _hud._UnhandledInput(RightButton(true));
            await Frames(20);
            Check(_hud.Wheel.WedgeLabels[4].Text == "✿\n2 Grow\nlearn 1 more\nfor a bridge" && _hud.Wheel.NotedWedges.SequenceEqual(new[] { 4 }), "with two learned the wedge's note is the bridge: " + _hud.Wheel.WedgeLabels[4].Text.Replace("\n", " "));
            _hud._UnhandledInput(RightButton(false));
            await Frames(2);
            _hud.ToggleMagicPanel();
            await Frames(14);
            var lines = _hud.GrowUnlocksText.Split('\n');
            Check(lines.Length == 4 && lines[0].StartsWith("✓ Growing without seeds: ", StringComparison.Ordinal) && lines[1].StartsWith("✓ Replanting: ", StringComparison.Ordinal) &&
                  lines[2] == "Locked: " + CommandHost.BridgeLocked && lines[3] == "Locked: " + CommandHost.IvyLocked,
                "the panel marks the two found and keeps the two words of the rest: " + _hud.GrowUnlocksText.Replace("\n", " | "));
            _hud.ToggleMagicPanel();
            var opened = string.Join("\n", JournalPages.OtherLines(host).Select(l => l.Text));
            var expertisePage = string.Join("\n", CampPages.ExpertiseLines(host).Select(l => l.Text));
            Check(opened.Contains("Grow\nTall plant\nThicket\nWhat they opened\nGrowing without seeds\nReplanting\n", StringComparison.Ordinal) && !opened.Contains("A bridge of plants", StringComparison.Ordinal) &&
                  expertisePage.Contains("What they opened\nGrowing without seeds\nReplanting", StringComparison.Ordinal),
                "the journal's Grow lines (and the camp's expertise page) name the unlocks found, and not the ones still locked");

            // ---- three: the bridge is open ----
            LearnGrow(host, 3);
            await Frames(2);
            Check(host.BridgeUnlocked && !host.IvyUnlocked, "(three learned: the bridge is open)");
            await Frames(2);
            Check(HelpLines().Any(l => l.Contains("hold 2 and sweep the aim from lip to lip across a gap for a bridge of plants", StringComparison.Ordinal)), "the help now teaches the drag");
            _growCasts.Clear();
            receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
            revision = host.Revision;
            _hud.AimRayForTests = Down(a);
            _hud._UnhandledInput(Press(Key.Key2));
            _hud.AimRayForTests = Down(lip);
            _hud._UnhandledInput(Release(Key.Key2));
            await Frames(3);
            Check(_growCasts.Count == 1 && _growCasts[0].Preview && !_growCasts[0].Ok && _growCasts[0].End is { } end && end.DistanceTo(lip) < 0.01f && _growCasts[0].Message == CommandHost.BridgeNoGap &&
                  _hud.NoticeText == CommandHost.BridgeNoGap && host.Revision == revision && _hud.Cue.State == "refused",
                $"the drag sends the host's preview of the line (its end {_growCasts.FirstOrDefault().End}); over solid floor the host says there is no gap, and nothing is cast: " + _hud.NoticeText);
            // A drag longer than the bridge is cut to 1.2 m from where it started (a direction where the cut end is on the floor, in the Gubble's reach and sight).
            _growCasts.Clear();
            Vector3? farHit = null, cutEnd = null;
            for (var i = 0; i < 8 && farHit == null; i++)
            {
                var direction = new Vector3(Mathf.Sin(i * Mathf.Pi / 4 + 0.2f), 0, Mathf.Cos(i * Mathf.Pi / 4 + 0.2f));
                if (GrowFloor(a + direction * 1.45f) is { } beyond && host.Room.Bounds.HasPoint(beyond + Vector3.Up * 0.001f) && GrowFloor(a + direction * 1.2f) is { } at12 &&
                    host.EffectSpot("grow", at12) is { InReach: true, InSight: true } && host.EffectSpot("grow", (a + at12) * 0.5f) is { InReach: true, InSight: true })
                    (farHit, cutEnd) = (beyond, at12);
            }
            Check(farHit != null, "(a floor spot 1.45 m along a drag whose 1.2 m end the Gubble reaches and sees)");
            if (farHit is { } pointer)
            {
                _hud.AimRayForTests = Down(a);
                _hud._UnhandledInput(Press(Key.Key2));
                _hud.AimRayForTests = Down(pointer);
                _hud._UnhandledInput(Release(Key.Key2));
                await Frames(3);
                Check(_growCasts.Count >= 1 && _growCasts[0].Preview && _growCasts[0].End is { } cut && Mathf.Abs(new Vector2(cut.X - a.X, cut.Z - a.Z).Length() - GrowKindLaws.BridgeMaxM) < 0.005f &&
                      new Vector2(cut.X - cutEnd!.Value.X, cut.Z - cutEnd.Value.Z).Length() < 0.01f,
                    $"a drag past 1.2 m asks for a bridge of 1.2 m ({(_growCasts.Count > 0 && _growCasts[0].End is { } c ? new Vector2(c.X - a.X, c.Z - a.Z).Length() : 0):0.000} m)");
            }
            // A drag of less than a hand's width is a tap.
            _growCasts.Clear();
            var near = a + along * 0.1f;
            _hud.AimRayForTests = Down(a);
            _hud._UnhandledInput(Press(Key.Key2));
            _hud.AimRayForTests = Down(near);
            _hud._UnhandledInput(Release(Key.Key2));
            await Frames(3);
            Check(_growCasts.Count >= 1 && _growCasts.All(c => c.End == null && !c.Preview), "a sweep of 10 cm is a tap: Grow at the press's spot, no end");
            // The Gubble out of reach of the bridge's middle comes closer first, with the line kept through the walk, then previews and casts it.
            var home = companion.GlobalPosition;
            var middle = (a + lip) * 0.5f;
            Vector3? farOff = null;
            foreach (var corner in new[] { new Vector3(-1.85f, 0, -1.35f), new Vector3(1.85f, 0, -1.35f), new Vector3(-1.85f, 0, 1.35f), new Vector3(1.85f, 0, 1.35f) })
                if (GrowFloor(corner) is { } floor && floor.DistanceTo(middle) > 2.2f && (farOff == null || floor.DistanceTo(middle) > farOff.Value.DistanceTo(middle))) farOff = floor;
            Check(farOff != null, "(a floor spot more than 2.2 m from the bridge's middle, beyond the Gubble's 2 m reach, for it to wait at)");
            if (farOff is { } waitAt)
            {
                Check(companion.TryTeleportTo(waitAt + Vector3.Up * 0.01f), "(the Gubble waits in a far corner)");
                host.PlayerGoal("stay");
                await Frames(10);
                _growCasts.Clear();
                Check(host.EffectSpot("grow", middle) is { InReach: false } && host.EffectSpot("grow", a) is { InReach: false }, "(the bridge's middle and lips are out of the Gubble's reach)");
                _hud.AimRayForTests = Down(a);
                _hud._UnhandledInput(Press(Key.Key2));
                _hud.AimRayForTests = Down(lip);
                _hud._UnhandledInput(Release(Key.Key2));
                await Frames(2);
                Check(_hud.CastWaiting && _growCasts.Count == 0, "out of its reach the Gubble is sent closer first and nothing is cast yet");
                for (var i = 0; i < 1200 && _growCasts.Count == 0; i++) await Frames(1);
                Check(!_hud.CastWaiting && _growCasts.Count >= 1 && _growCasts[0].Preview && _growCasts[0].End is { } walked && walked.DistanceTo(lip) < 0.01f && _hud.NoticeText == CommandHost.BridgeNoGap,
                    $"on arrival it previews the line it was sent for, lip to lip ({_growCasts.Count} casts; {_hud.NoticeText})");
                companion.TryTeleportTo(home);
                host.PlayerGoal("follow");
                await Frames(10);
            }
            // The outline of an accepted preview: the band from lip to lip, flashing, then gone (the suite has no gap for the host to accept).
            var outline = new JsonObject
            {
                ["kind"] = GrowKindWords.Bridge, ["center_m"] = new JsonArray(a.X, a.Y, a.Z), ["end_m"] = new JsonArray(lip.X, lip.Y, lip.Z), ["radius_m"] = 0.5,
            };
            _hud.FlashGrowOutlineForTests(outline);
            Check(_hud.FireFlashing && look is { FirePreviewVisible: true, PreviewIsLine: true }, "a preview's lips draw the deck's outline as a band");
            for (var i = 0; i < 120 && _hud.FireFlashing; i++) await Frames(1);
            Check(!_hud.FireFlashing && look is { FirePreviewVisible: false }, "and it is gone in a moment");

            // ---- five: nothing left to unlock ----
            LearnGrow(host, 5);
            _hud.AimRayForTests = Down(FloorNearGubble(.3f));
            _hud._UnhandledInput(RightButton(true));
            await Frames(20);
            Check(_hud.Wheel.WedgeLabels[4].Text == "✿\n2 Grow" && _hud.Wheel.NotedWedges.Count == 0, "with all four found the wedge is plain again: " + _hud.Wheel.WedgeLabels[4].Text.Replace("\n", " "));
            _hud._UnhandledInput(RightButton(false));
            await Frames(2);
            _hud.ToggleMagicPanel();
            await Frames(14);
            Check(_hud.GrowUnlocksText.Split('\n').All(line => line.StartsWith("✓ ", StringComparison.Ordinal)) && _hud.GrowUnlocksText.Contains("Hold 2 and drag from lip to lip", StringComparison.Ordinal),
                "and the panel lists all four as found: " + _hud.GrowUnlocksText.Replace("\n", " | "));
            _hud.ToggleMagicPanel();
            var all = string.Join("\n", JournalPages.OtherLines(host).Select(l => l.Text));
            Check(all.Contains("What they opened\nGrowing without seeds\nReplanting\nA bridge of plants\nWall ivy", StringComparison.Ordinal), "the journal names all four");
            LearnGrow(host, 0);
            Check(!string.Join("\n", JournalPages.OtherLines(host).Select(l => l.Text)).Contains("What they opened", StringComparison.Ordinal), "and with none learned it names none");
        }
        finally
        {
            host.Handled -= ListenForGrow;
            host.NatureSampleForTests = previousSample;
            _hud.AimRayForTests = null;
            _hud.LightForTests = null;
            host.ConfigureGlowProgress(new LocalGlowProgressStore(ProjectSettings.GlobalizePath(HudGlowProfile)));
        }
    }
}
