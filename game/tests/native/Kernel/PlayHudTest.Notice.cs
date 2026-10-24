using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Notices where the eye is (the second playtest round's P2, item 4; RoomHud.Notice.cs): a notice shows in its own card under the reticle,
/// held towards the middle of the screen (clear of the meters and the minimap), fades after about four seconds, shows again when the same
/// words are said again, stands aside while the journal is open, and reaches the record as it shows and as it fades. Measured by numbers:
/// nobody can look at a screen here.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestNotices()
    {
        var screen = new Vector2(1280, 720);
        var size = new Vector2(300, 40);
        var middle = RoomHud.NoticePlace(screen * 0.5f, size, screen);
        var corner = RoomHud.NoticePlace(Vector2.Zero, size, screen);
        var low = RoomHud.NoticePlace(screen, size, screen);
        Check(middle == new Vector2(490, 360 + RoomHud.NoticeBelowReticlePx), $"with the reticle in the middle the card is centred just under it: {middle}");
        Check(corner.X >= screen.X * RoomHud.NoticeEdgeShareX - size.X * 0.5f - 0.01f && corner.Y >= screen.Y * RoomHud.NoticeEdgeShareTop + RoomHud.NoticeBelowReticlePx - 0.01f &&
              !new Rect2(corner, size).Intersects(new Rect2(0, 0, 280, 140)) && !new Rect2(corner, size).Intersects(new Rect2(screen.X - 300, 0, 300, 240)),
            $"with the reticle at the upper left the card is held towards the middle, clear of the meters' corner and the minimap's: {corner}");
        Check(new Rect2(low, size).End.X <= screen.X - 16 && new Rect2(low, size).End.Y <= screen.Y - 16 && low.Y < screen.Y * (1 - RoomHud.NoticeEdgeShareBottom) + RoomHud.NoticeBelowReticlePx + 0.01f,
            $"with the reticle at the lower right the card stays on the screen, off the edges: {low}");
        Check(Math.Abs(RoomHud.NoticeLifeFor("Short words.") - 4.0) < 1e-9 && RoomHud.NoticeLifeFor(new string('a', 400)) == RoomHud.NoticeLongestS,
            "a notice stands about four seconds (3.2 s, then a 0.8 s fade); a long one longer, at most 8 s");

        // In the room: Shift+P says its words (the motion profile's), the same each time.
        var shown = new List<ScreenNotice>();
        void Watch(ScreenNotice notice) { if (notice.Channel == "hud") shown.Add(notice); }
        _hud.NoticeShown += Watch;
        try
        {
            for (var i = 0; i < 300 && _hud.NoticeShowing; i++) await Frames(1);
            _hud._UnhandledInput(Press(Key.P, shift: true));
            await Frames(1);
            var card = _hud.NoticeCard;
            var words = _hud.NoticeText;
            var rect = card.GetGlobalRect();
            var view = _hud.GetViewport().GetVisibleRect().Size;
            var meters = _hud.MeterPanel.GetGlobalRect();
            Check(card.Visible && words.Length > 0 && ((Label)card.FindChild("Notice", true, false)!).Text == words && card.Modulate.A == 1,
                "a notice shows at once in its own card: " + words);
            Check(rect.Position.X >= 16 && rect.End.X <= view.X - 16 && rect.Position.Y >= view.Y * RoomHud.NoticeEdgeShareTop && rect.End.Y <= view.Y - 16 &&
                  !rect.Intersects(meters) && !rect.Intersects(new Rect2(view.X - 300, 0, 300, 240)),
                $"under the reticle, towards the middle of the screen, clear of the meters ({meters.Position.X:0},{meters.Position.Y:0} {meters.Size.X:0}x{meters.Size.Y:0}) and the minimap: card at {rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0} of {view.X:0}x{view.Y:0}");
            Check(shown.LastOrDefault().Text == words, "the record sees it as it shows");
            await Frames(150);
            var held = card.Visible && card.Modulate.A == 1;
            await Frames(66);
            var fading = card.Visible && card.Modulate.A is > 0 and < 1;
            var fadingAlpha = card.Modulate.A;
            await Frames(30);
            Check(held && fading && !card.Visible && !_hud.NoticeShowing,
                $"it holds for 2.5 s, is fading at 3.6 s (alpha {fadingAlpha:0.00}) and is gone by 4.1 s");
            Check(shown.LastOrDefault().Text == "", "and the record sees the line cleared as it fades");
            // The same words again show again (they are a new notice, not a stale line).
            _hud._UnhandledInput(Press(Key.P, shift: true));
            await Frames(1);
            Check(card.Visible && _hud.NoticeText == words && shown.Count(n => n.Text == words) == 2, "the same words said again show again");
            // The journal fills the screen: the card stands aside, and comes back while its time lasts.
            _hud.OpenJournal();
            await Frames(2);
            var aside = !card.Visible;
            _hud.CloseJournal();
            for (var i = 0; i < 120 && _hud.JournalOpen; i++) await Frames(1);
            await Frames(1);
            Check(aside && (card.Visible || !_hud.NoticeShowing), "it stands aside while the journal is open");
        }
        finally { _hud.NoticeShown -= Watch; }
    }
}
