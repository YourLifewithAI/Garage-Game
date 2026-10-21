using Godot;

namespace GarageGame.Native;

/// <summary>
/// The swim and dive hint (the first recorded play's debrief, 16 October, Change 1: "There's no dive button that I'm aware of"; it is
/// Ctrl, named only inside the H help). When the body enters deep water, a small tag stands where the Gubble's ask tag does, by the
/// reticle ("right-click: go and look"), just under it when both show: "Deep water: Space leaps · hold Ctrl to dive · B home", its keys
/// read from the input map (a remapped key shows at once). Shown once for each entry into deep water (the body's swim, which begins at
/// a depth the shallows never reach and ends where it finds its feet), kept while the body swims, and gone as it leaves the water; it
/// stands aside, still counted as shown, while a panel, the wheel or the Gubble's eyes hold the screen. Both ends reach the playtest
/// record as the HUD's notices (the hint's words, then an empty line), so the next debrief can see whether the dive came after the hint.
/// </summary>
public partial class RoomHud
{
    private Label _swimHint = null!;
    private bool _swimHintEntered;

    /// <summary>The hint's tag (its words, and whether it shows).</summary>
    public Label SwimHint => _swimHint;
    /// <summary>Whether the body is in deep water and was told so on entering it (a panel may hold the tag out of sight meanwhile).</summary>
    public bool SwimHintEntered => _swimHintEntered;

    /// <summary>The hint's words: the keys are the map's own (the body's leap, its dive, the way home).</summary>
    public string SwimHintWords() => $"Deep water: {K(Act.Jump)} leaps · hold {K(Act.MoveDive)} to dive · {K(Act.BodyHome)} home";

    private void BuildSwimHint(Theme compactTheme)
    {
        _swimHint = new Label { Name = "SwimHint", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = compactTheme };
        AddChild(_swimHint);
        _keyTexts.Add((words => _swimHint.Text = words, SwimHintWords));
    }

    /// <summary>Every frame: the body enters or leaves deep water, and the tag shows, hides or follows the reticle.</summary>
    private void UpdateSwimHint()
    {
        var swimming = Player.IsSwimming && !IntroPlaying;
        if (swimming != _swimHintEntered)
        {
            _swimHintEntered = swimming;
            NoticeShown?.Invoke(new ScreenNotice("hud", swimming ? SwimHintWords() : ""));
        }
        var shown = swimming && !ReadingPanelOpen && !_wheel.Held && !ChoosingMagicSpot;
        if (_swimHint.Visible != shown) _swimHint.Visible = shown;
        if (!shown) return;
        var size = _swimHint.GetCombinedMinimumSize();
        var screen = GetViewport().GetVisibleRect().Size;
        // Under the ask tag (also at the reticle) when that shows, else in its place.
        var under = _askTag.Visible ? _askTag.GetCombinedMinimumSize().Y + 2 : 0;
        var at = AimScreenPoint() + new Vector2(16, 12 + under);
        _swimHint.Position = new Vector2(Mathf.Clamp(at.X, 0, Mathf.Max(0, screen.X - size.X)), Mathf.Clamp(at.Y, 0, Mathf.Max(0, screen.Y - size.Y)));
    }
}
