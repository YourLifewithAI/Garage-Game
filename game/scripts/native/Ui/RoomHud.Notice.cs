using System;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Notices where the eye is (the second recorded play: "I don't remember seeing these messages"; the line sat at the bottom edge under
/// "H keys" and kept its last words for minutes). A notice now shows in a small card just under the reticle (the cursor, which is the
/// aim in every view), held towards the middle of the screen when the cursor is off at an edge so it never covers the meters at the
/// upper left or the minimap at the upper right, and fades after about four seconds (NoticeHoldS then NoticeFadeS; a long one stays a
/// little longer, up to NoticeLongestS, so it can be read), to the right of a panel open on the left (the camp, the Magic panel, the
/// settings). The same words said again show again. It stands aside while the journal or
/// the Gubble's eyes fill the screen. The bottom line keeps the key hints only. The playtest record sees each notice as it shows and an
/// empty line as it fades (the reader can tell how long it stood).
/// </summary>
public partial class RoomHud
{
    /// <summary>A notice shows fully this long, then fades over NoticeFadeS: gone after about four seconds (provisional).</summary>
    public const double NoticeHoldS = 3.2, NoticeFadeS = 0.8;
    /// <summary>A long notice holds longer, a second for every NoticeCharsPerS characters past NoticeShortChars, up to NoticeLongestS in all.</summary>
    public const int NoticeShortChars = 80;
    public const double NoticeCharsPerS = 25, NoticeLongestS = 8;
    /// <summary>The card's widest, and how far under the reticle it sits (under the ask tag and the swim hint), in pixels.</summary>
    public const float NoticeMaxWidthPx = 520, NoticeBelowReticlePx = 58;
    /// <summary>The share of the screen, from each side, that the card's anchor is held inside (the middle, where the eye rests).</summary>
    public const float NoticeEdgeShareX = 0.22f, NoticeEdgeShareTop = 0.26f, NoticeEdgeShareBottom = 0.2f;

    private PanelContainer _noticeCard = null!;
    private Label _notice = null!;
    private string _noticeWords = "";
    private int _noticeSerial, _noticeSerialShown;
    private double _noticeAge = -1, _noticeLifeS;

    /// <summary>
    /// The notice's words. Every part of the HUD writes them here; each write is a notice to show (the same words twice show twice), so it
    /// is a property that counts the writes.
    /// </summary>
    private string _noticeText
    {
        get => _noticeWords;
        set
        {
            _noticeWords = value ?? "";
            _noticeSerial++;
        }
    }

    /// <summary>The card the notice shows in (its place, size and fade), for the HUD suite.</summary>
    public PanelContainer NoticeCard => _noticeCard;
    /// <summary>Whether a notice shows now, and how far through its life it is (0 shown, 1 gone).</summary>
    public bool NoticeShowing => _noticeAge >= 0;
    public double NoticeLife => _noticeAge < 0 ? 1 : Math.Clamp(_noticeAge / _noticeLifeS, 0, 1);

    private void BuildNotice(Theme compactTheme)
    {
        _noticeCard = new PanelContainer { Name = "NoticeCard", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = compactTheme };
        _noticeCard.AddThemeStyleboxOverride("panel", MenuStyle.Box(new Color(0.075f, 0.13f, 0.13f, 0.9f), MenuStyle.Warm, 1, 10, 10));
        _notice = new Label
        {
            Name = "Notice", Text = _noticeText, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        _notice.AddThemeFontSizeOverride("font_size", 17);
        _noticeCard.AddChild(_notice);
        AddChild(_noticeCard);
    }

    /// <summary>How long a notice of these words stands, fade included: about four seconds, longer for a long one.</summary>
    public static double NoticeLifeFor(string words) =>
        Math.Min(NoticeLongestS, NoticeHoldS + NoticeFadeS + Math.Max(0, words.Length - NoticeShortChars) / NoticeCharsPerS);

    /// <summary>Each frame: new words show (placed under the reticle), and a notice standing fades when its time is up.</summary>
    private void UpdateNotice(double delta)
    {
        if (_noticeSerial != _noticeSerialShown)
        {
            _noticeSerialShown = _noticeSerial;
            ShowNotice(_noticeWords);
        }
        if (_noticeAge < 0) return;
        _noticeAge += delta;
        var fadeFrom = _noticeLifeS - NoticeFadeS;
        var alpha = _noticeAge <= fadeFrom ? 1f : (float)Math.Max(0, (_noticeLifeS - _noticeAge) / NoticeFadeS);
        if (!Mathf.IsEqualApprox(_noticeCard.Modulate.A, alpha)) _noticeCard.Modulate = new Color(1, 1, 1, alpha);
        var aside = JournalOpen || GubbleViewOpen || IntroPlaying;
        if (_noticeCard.Visible == aside) _noticeCard.Visible = !aside;
        if (_noticeAge < _noticeLifeS) return;
        _noticeAge = -1;
        _noticeCard.Visible = false;
        // The record: the line cleared as it faded.
        NoticeLineShown("");
    }

    /// <summary>The words show now: under the reticle, held towards the middle of the screen, at full strength.</summary>
    private void ShowNotice(string words)
    {
        _notice.Text = words;
        NoticeLineShown(words);
        if (words.Length == 0)
        {
            _noticeAge = -1;
            _noticeCard.Visible = false;
            return;
        }
        _noticeLifeS = NoticeLifeFor(words);
        _noticeAge = 0;
        _noticeCard.Modulate = Colors.White;
        // Wide words wrap within the card's widest.
        _notice.AutowrapMode = TextServer.AutowrapMode.Off;
        _notice.CustomMinimumSize = Vector2.Zero;
        _noticeCard.ResetSize();
        if (_noticeCard.GetCombinedMinimumSize().X > NoticeMaxWidthPx)
        {
            _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _notice.CustomMinimumSize = new Vector2(NoticeMaxWidthPx - 24, 0);
            _noticeCard.ResetSize();
        }
        PlaceNotice();
        _noticeCard.Visible = !(JournalOpen || GubbleViewOpen || IntroPlaying);
    }

    /// <summary>The card's place: centred under the reticle, the reticle held inside the middle of the screen, the card kept on it.</summary>
    private void PlaceNotice()
    {
        var screen = GetViewport().GetVisibleRect().Size;
        var size = _noticeCard.GetCombinedMinimumSize();
        _noticeCard.Size = size;
        // Clear of a panel open on the left (the camp, the Magic panel, the settings): the card stands to its right; and above the hotbar.
        var leftClear = 0f;
        foreach (var panel in new Control[] { _campScreen, _magicPanel, _settings })
            if (panel is { Visible: true }) leftClear = Mathf.Max(leftClear, panel.GetGlobalRect().End.X + 16);
        _noticeCard.Position = NoticePlace(AimScreenPoint(), size, screen, leftClear, _hotbar != null && _hotbar.Visible ? screen.Y - BarClearTop(screen) : 0);
    }

    /// <summary>
    /// Where a notice card of this size goes for a reticle at this point on a screen of this size (the suite checks it by numbers): bottomClear
    /// is how much of the screen's foot it keeps off (the hotbar and its caption).
    /// </summary>
    public static Vector2 NoticePlace(Vector2 reticle, Vector2 size, Vector2 screen, float leftClear = 0, float bottomClear = 0)
    {
        var anchor = new Vector2(
            Mathf.Clamp(reticle.X, screen.X * NoticeEdgeShareX, screen.X * (1 - NoticeEdgeShareX)),
            Mathf.Clamp(reticle.Y, screen.Y * NoticeEdgeShareTop, screen.Y * (1 - NoticeEdgeShareBottom)));
        var at = new Vector2(Mathf.Max(anchor.X - size.X * 0.5f, leftClear), anchor.Y + NoticeBelowReticlePx);
        var foot = Mathf.Max(16, bottomClear);
        return new Vector2(Mathf.Clamp(at.X, 16, Mathf.Max(16, screen.X - size.X - 16)), Mathf.Clamp(at.Y, 16, Mathf.Max(16, screen.Y - size.Y - foot)));
    }
}
