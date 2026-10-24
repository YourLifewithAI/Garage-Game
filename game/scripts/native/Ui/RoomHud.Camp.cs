using System;
using System.Collections.Generic;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Fire's camp on screen (MECHANICS.md decisions 61 and 70; docs/runs/RUN-2-OPENING.md's chime and card):
/// <list type="bullet">
/// <item><b>The rest key</b> (Z, the input map's rest): sit down by a lit campfire, through the host's PlayerRest, which checks the body
/// itself; pressed again, or as soon as the body moves, the player stands. The sit is <b>the rest crouch</b>: the body's drawing
/// lowered to RestCrouch of its height about the feet, a stand-in until the characters' lane draws a real sitting pose.</item>
/// <item><b>The expertise announcement</b>, for every expertise the Gubble learns (the host's TakeLearned, each once): a short
/// generated chime (two soft notes, no asset) and a two-line card, "&lt;name&gt; has learned &lt;expertise&gt;." over "What will you do
/// with it?", the expertise named as the journal names it. Never shown or heard headless; one card at a time, in the order learned.</item>
/// </list>
/// </summary>
public partial class RoomHud
{
    /// <summary>The rest crouch: the drawn body this much of its height while sitting (provisional).</summary>
    public const float RestCrouch = 0.6f;
    /// <summary>How long a card shows, with its fade in and out, in seconds (provisional).</summary>
    public const float CardSeconds = 5f, CardFadeInS = 0.4f, CardFadeOutS = 0.8f;
    public const string CardQuestion = "What will you do with it?";

    /// <summary>Test seam: shows the card (and plays the chime) even headless.</summary>
    internal static bool CardsHeadlessForTests { get; set; }

    private bool _sitting;
    private readonly Dictionary<Node3D, Transform3D> _standingPose = new();
    private readonly Queue<string> _cards = new();
    private PanelContainer? _card;
    private Label? _cardTitle, _cardQuestion;
    private AudioStreamPlayer? _chime;
    private double _cardAge = -1;

    /// <summary>Whether the player sits by a fire now (the rest crouch is on).</summary>
    public bool Sitting => _sitting;
    /// <summary>The card now: its two lines, whether it shows, and how many cards and chimes have played this session.</summary>
    public string CardText => _cardTitle == null ? "" : _cardTitle.Text + "\n" + _cardQuestion!.Text;
    public bool CardVisible => _card is { Visible: true };
    public int CardsShown { get; private set; }
    public int ChimesPlayed { get; private set; }

    /// <summary>The card's first line for an expertise: the companion's name, then the expertise as the journal names it.</summary>
    public string CardTitleFor(string ability, string id) => $"{Companion.NameTag} has learned {JournalPages.ExpertiseTitle(ability, id)}.";

    /// <summary>The rest key: sit down by a lit campfire, or stand up again.</summary>
    public RestOutcome? RestKey()
    {
        if (Host is not { } host) { _noticeText = "The command host is not attached; the rest key is off."; return null; }
        // E's prompt is asked again next frame (stand up, or sit).
        _focusAge = double.MaxValue;
        if (_sitting)
        {
            Stand(host);
            _noticeText = "You stand up.";
            return null;
        }
        var outcome = host.PlayerRest();
        _noticeText = outcome.Words;
        if (outcome.Ok) SitDown();
        return outcome;
    }

    private void SitDown()
    {
        _sitting = true;
        _standingPose.Clear();
        if (Player.GetNodeOrNull<Node3D>(PrototypeBodyPath) is not { } drawing) return;
        var crouch = new Transform3D(Basis.FromScale(new Vector3(1, RestCrouch, 1)), Vector3.Zero);
        foreach (var child in drawing.GetChildren())
        {
            if (child is not Node3D part) continue;
            _standingPose[part] = part.Transform;
            part.Transform = crouch * part.Transform;
        }
    }

    private void Stand(CommandHost? host)
    {
        _sitting = false;
        foreach (var (part, pose) in _standingPose)
            if (IsInstanceValid(part)) part.Transform = pose;
        _standingPose.Clear();
        host?.PlayerStand();
    }

    private static readonly NodePath PrototypeBodyPath = "OriginalPrototypeBody";

    /// <summary>Each frame: the sit ends once the host says the player no longer rests (they moved); the next card when one waits.</summary>
    private void TickCamp(double delta)
    {
        var host = Host;
        if (_sitting && (host == null || !host.PlayerResting)) Stand(host);
        while (host != null && host.TakeLearned(out var ability, out var id))
            if (ShowsCards && _cards.Count < 16) _cards.Enqueue(CardTitleFor(ability, id));
        if (_cardAge >= 0)
        {
            _cardAge += delta;
            var alpha = _cardAge < CardFadeInS ? (float)(_cardAge / CardFadeInS) : _cardAge > CardSeconds - CardFadeOutS ? (float)Math.Max(0, (CardSeconds - _cardAge) / CardFadeOutS) : 1f;
            _card!.Modulate = new Color(1, 1, 1, alpha);
            if (_cardAge < CardSeconds) return;
            _cardAge = -1;
            _card.Visible = false;
        }
        if (_cards.Count > 0) ShowCard(_cards.Dequeue());
    }

    /// <summary>Cards and chimes are for a window: never headless (but for a test's seam).</summary>
    private static bool ShowsCards => HasWindow || CardsHeadlessForTests;

    private void ShowCard(string title)
    {
        if (_card == null)
        {
            _card = new PanelContainer { Name = "ExpertiseCard", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _card.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _card.GrowHorizontal = Control.GrowDirection.Both;
            _card.OffsetTop = 90;
            var lines = new VBoxContainer { Name = "Lines", Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            _card.AddChild(lines);
            _cardTitle = new Label { Name = "Title", HorizontalAlignment = HorizontalAlignment.Center };
            _cardTitle.AddThemeFontSizeOverride("font_size", 28);
            _cardQuestion = new Label { Name = "Question", HorizontalAlignment = HorizontalAlignment.Center, Text = CardQuestion };
            _cardQuestion.AddThemeFontSizeOverride("font_size", 20);
            lines.AddChild(_cardTitle);
            lines.AddChild(_cardQuestion);
            AddChild(_card);
        }
        _cardTitle!.Text = title;
        _card.Modulate = new Color(1, 1, 1, 0);
        _card.Visible = true;
        _cardAge = 0;
        CardsShown++;
        PlayChime();
    }

    /// <summary>The announcement's chime: two soft rising notes, generated here (no sound asset), the same for every expertise.</summary>
    private void PlayChime()
    {
        if (_chime == null)
        {
            const int rate = 22050;
            const double duration = 0.9;
            var count = (int)(rate * duration);
            var data = new byte[count * 2];
            for (var index = 0; index < count; index++)
            {
                var time = (double)index / rate;
                // G5 for the first third, then C6, each a struck tone that fades.
                var (frequency, start) = time < 0.28 ? (783.99, 0.0) : (1046.5, 0.28);
                var local = time - start;
                var envelope = Math.Min(1, local * 60) * Math.Exp(-local * 5);
                var sample = (short)(5200 * envelope * (Math.Sin(2 * Math.PI * frequency * time) + 0.3 * Math.Sin(4 * Math.PI * frequency * time)));
                data[index * 2] = (byte)(sample & 255);
                data[index * 2 + 1] = (byte)((sample >> 8) & 255);
            }
            _chime = new AudioStreamPlayer
            {
                Name = "ExpertiseChime", VolumeDb = -8,
                Stream = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data },
            };
            AddChild(_chime);
        }
        _chime.Play();
        ChimesPlayed++;
    }
}
