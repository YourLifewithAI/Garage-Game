using System.Linq;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The game's one journal (the founder's brief: a small leather journal at the lower right, expanding and opening to the index,
/// then closing and shrinking back). Its key (J, from the input map) or a click on the book opens it; its key, Esc or its close
/// link close it. While it is open, opening or closing, the book holds the controls: the body stands still, every key but the
/// journal's and Esc waits (ControlContext.Reading), the Gubble's ask, wheel and the look drag cannot start, and the shade under
/// the pages takes the mouse. Opening it closes the appearance and Magic panels; it replaces the separate Glow journal panel,
/// whose words are now the Discoveries page (Ui/JournalPages.cs). Its pages are written from the host's data.
/// </summary>
public partial class RoomHud
{
    /// <summary>The help footer stops this far from the right edge, clear of the book's dock.</summary>
    public const float JournalDockClearancePx = 128;
    private JournalPresentation _book = null!;

    /// <summary>The book: open, opening or closing (it holds the controls until it is back in its dock).</summary>
    public bool JournalOpen => _book != null && _book.IsActive;
    public JournalPresentation Book => _book;
    public JournalPanel Journal => _book.Panel;
    /// <summary>The words on the journal's page now, also readable by the headless HUD suite.</summary>
    public string JournalText => _book?.Panel.PageText ?? "";

    private void BuildJournal()
    {
        _book = new JournalPresentation
        {
            FindHost = () => Host, KeyLabel = () => K(Act.Journal), DockPressed = ToggleJournal, ActivityChanged = JournalActivityChanged,
            ButtonPressed = JournalButton,
        };
        AddChild(_book);
        // The help footer stops short of the dock, and its long lines (the help, a long notice) wrap within it instead of
        // running on under the book.
        _footer.OffsetRight = -JournalDockClearancePx;
        // (Not the labels in a row of buttons: a wrapping label in a row has no width of its own.)
        foreach (var label in _footer.FindChildren("*", "Label", true, false).OfType<Label>())
            if (label.GetParent() is not HBoxContainer) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    /// <summary>The journal's key or a click on the book: open it at the index, or close it (a moving book turns back).</summary>
    public void ToggleJournal()
    {
        if (_book.WantsOpen) CloseJournal();
        else OpenJournal();
    }

    /// <summary>Open the book at its index, closing the other panels and calling off the wheel, a drag or a spot being chosen.</summary>
    public void OpenJournal()
    {
        CancelAsk();
        EndDrag();
        if (_pickingMagicSpot) _noticeText = "Spot selection cancelled.";
        _pickingMagicSpot = false;
        _movingGlow = null;
        if (SettingsOpen) ToggleSettings();
        if (MagicPanelOpen) SetMagicPanelOpen(false);
        // The book pulsed to be opened (decision 53): it is, and it is quiet until the Fractals in hand next reach five.
        StopJournalPulse();
        _book.Open();
    }

    /// <summary>Close the book: it shrinks back to its dock, and gives the controls back when it gets there.</summary>
    public void CloseJournal() => _book.Close();

    /// <summary>Another panel takes over: the book goes straight back to its dock.</summary>
    private void CloseJournalNow()
    {
        if (_book != null && _book.IsActive) _book.Close(instant: true);
    }

    /// <summary>The book took or gave back the controls.</summary>
    private void JournalActivityChanged()
    {
        Player.SetInputEnabled(!ReadingPanelOpen);
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>
    /// The journal's keys, before any other (RoomHud._UnhandledInput): its key opens or closes the book from anywhere; while the
    /// book is open or moving, Esc closes it and every other key waits. True when the press was the book's.
    /// </summary>
    private bool HandleJournalKeys(InputEvent press)
    {
        if (PlayerControls.Hit(press, Act.Journal))
        {
            ToggleJournal();
            return true;
        }
        if (!JournalOpen) return false;
        if (PlayerControls.Hit(press, Act.MouseRelease)) CloseJournal();
        return true;
    }
}
