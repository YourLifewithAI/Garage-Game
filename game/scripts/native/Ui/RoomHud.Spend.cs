using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Spending Fractals in play (decision 41 of docs/game/MECHANICS.md; the host's fractal.spend, Ui/FractalSpend.cs): no key of its
/// own. The journal's Character page offers the two buttons at the camp, and a button sends the player's own command through
/// the host's one command path like every other player action here (Gather, Hands); the host decides, and its words (the new heart,
/// or its child-friendly refusal) are the HUD's notice and the page's foot. A short hint points to the journal when the player
/// stands at the camp with enough in hand, and the journal's book in its dock pulses (decision 53) from the moment five are in
/// hand and a spend is possible until the count falls under five or the book is opened.
/// </summary>
public partial class RoomHud
{
    /// <summary>The hint is looked for this often (a few times a second is plenty for a player who stands still at the camp).</summary>
    public const double SpendHintEveryS = 0.25;
    /// <summary>The pulse looks at the Fractals in hand this often (the same few times a second as the hint; opening the book ends it at once).</summary>
    public const double JournalPulseEveryS = SpendHintEveryS;
    private double _spendHintAge;
    private bool _spendHintShown;
    private double _pulseAge;
    private bool _pulseArmed;
    private bool _fiveInHand;

    /// <summary>Whether the journal's book is pulsing in its dock now (decision 53: five Fractals in hand and a spend possible, the book not yet opened).</summary>
    public bool JournalPulsing => _book != null && _book.Dock.Pulsing;

    /// <summary>The words of the last spend hint (empty until one was given), and how many have been given, for the suite.</summary>
    public string SpendHint { get; private set; } = "";
    public int SpendHintCount { get; private set; }

    /// <summary>
    /// A button on a journal page asked for something (JournalPanel.ButtonPressed): the Fractals' two buttons spend. The words it
    /// answers with go to the page's foot.
    /// </summary>
    private string JournalButton(string action) => action switch
    {
        FractalSpend.HeartTarget => SpendFractals(FractalSpend.HeartGrow),
        FractalSpend.BandTarget => SpendFractals(FractalSpend.BandGrow),
        _ => "",
    };

    /// <summary>
    /// Spend five Fractals (the pack's price) for "heart" or "band": the player's fractal.spend. The host decides (at the camp, with
    /// enough in hand, below the cap); what it answers is the notice. Returns the words.
    /// </summary>
    public string SpendFractals(string grow)
    {
        var words = Host is { } host ? FractalSpend.Spend(host, grow) : "The command host is not attached; spending is off.";
        _noticeText = words;
        return words;
    }

    /// <summary>
    /// Whether the player stands at a camp, as the host decides it (the room's player spawn or any lit campfire, within
    /// CommandHost.CampRadiusM; Fire's camp). The host's question allocates nothing once its camps are known, so the HUD asks it
    /// directly, a few times a second for as long as the player holds enough Fractals to spend.
    /// </summary>
    public bool AtCamp(Kernel.CommandHost host) => host.PlayerAtCamp();

    /// <summary>
    /// Each frame (from the meters' update): a few times a second, the hint when the player has just got to the camp with enough
    /// Fractals in hand. Once for each arrival: it comes again after the player has been away or has spent the Fractals down.
    /// While the book is open the player is already where the buttons are, so no hint is given.
    /// </summary>
    private void TickSpendHint(Kernel.CommandHost host, double delta)
    {
        _spendHintAge += delta;
        if (_spendHintAge < SpendHintEveryS) return;
        _spendHintAge = 0;
        if (!FractalSpend.CanAfford(host) || !AtCamp(host))
        {
            _spendHintShown = false;
            return;
        }
        if (_spendHintShown) return;
        _spendHintShown = true;
        // A held island's notice says why it is held (RoomHud._Process); nothing is said over it.
        if (JournalOpen || host.IslandHeld) return;
        SpendHintCount++;
        _noticeText = SpendHint = FractalSpend.HintWords(host, K(Act.Journal), K(Act.Interact));
    }

    // ---- the journal's pulse (decision 53) ----

    /// <summary>
    /// Each frame (from the meters' update; host null while the intro plays or no host is attached). The book pulses from the
    /// moment the Fractals in hand pay for a spend that can still be made (FractalSpend.CanAfford: five, below the caps; at the
    /// camp or not, for the book is how the player finds out) and stops when the count falls under five, when the book is opened
    /// (at once, however it was opened), or when the island is held (its spends are refused). It does not come back when the book
    /// is closed again: the next pulse is the next time the count reaches five. The Fractals are looked at a few times a second,
    /// allocating nothing; the breath itself is a few property writes a frame.
    /// </summary>
    private void TickJournalPulse(Kernel.CommandHost? host, double delta)
    {
        if (_book == null) return;
        if (host == null || IntroPlaying)
        {
            // Not yet playing: nothing pulses, and the count in hand now (the intro over) is a pulse to come.
            _pulseArmed = false;
            _fiveInHand = false;
        }
        else
        {
            if (JournalOpen) _pulseArmed = false;
            _pulseAge += delta;
            if (_pulseAge >= JournalPulseEveryS)
            {
                _pulseAge = 0;
                var spendable = !host.IslandHeld && FractalSpend.CanAfford(host);
                if (!spendable) _pulseArmed = false;
                else if (!_fiveInHand && !JournalOpen) _pulseArmed = true;
                _fiveInHand = spendable;
            }
        }
        _book.Dock.SetPulsing(_pulseArmed);
        _book.Dock.Animate(delta);
    }

    /// <summary>The book was opened: the pulse is over for these Fractals.</summary>
    private void StopJournalPulse()
    {
        _pulseArmed = false;
        _book?.Dock.SetPulsing(false);
    }
}
