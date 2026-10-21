using Godot;

namespace GarageGame.Native;

/// <summary>
/// Hold the left button and drag to look around (the Glow playtest, 9 October: "we'll want to be able to move the mouse freely
/// around the scene"). The cursor is always free and is the aim in every view. A press only arms the drag: the pointer must
/// move StartPx before the view turns, so a click without a drag does nothing. Once dragging, the HUD hides and holds the
/// cursor (the captured mouse, so the view turns as far as the hand moves) and turns the view by the motion; on release the
/// cursor comes back where it was pressed (Anchor), which stays the aim while it is hidden. RoomHud carries the drag out.
/// </summary>
public sealed class LookDrag
{
    /// <summary>How far the pointer moves (screen pixels, from the press) before a press becomes a drag: below it, a click.</summary>
    public const float StartPx = 4f;

    /// <summary>Whether the drag button is down (a drag may not have started yet).</summary>
    public bool Pressed { get; private set; }
    /// <summary>Whether the view is being turned: the cursor is hidden.</summary>
    public bool Dragging { get; private set; }
    /// <summary>Where the button went down: the cursor comes back here, and it is the aim while the cursor is hidden.</summary>
    public Vector2 Anchor { get; private set; }

    private Vector2 _moved;

    /// <summary>The button went down at this screen point.</summary>
    public void Press(Vector2 at)
    {
        Pressed = true;
        Dragging = false;
        Anchor = at.IsFinite() ? at : Vector2.Zero;
        _moved = Vector2.Zero;
    }

    /// <summary>
    /// The pointer moved (relative pixels). Returns how far to turn the view now: nothing before StartPx; from the move that
    /// crosses it, the motion itself. startedNow says this move started the drag (the HUD then hides the cursor).
    /// </summary>
    public Vector2 Move(Vector2 relative, out bool startedNow)
    {
        startedNow = false;
        if (!Pressed || !relative.IsFinite()) return Vector2.Zero;
        if (!Dragging)
        {
            _moved += relative;
            if (_moved.Length() < StartPx) return Vector2.Zero;
            Dragging = true;
            startedNow = true;
        }
        return relative;
    }

    /// <summary>The button let go (or the drag was called off): true when it was a drag, so the cursor must come back to Anchor.</summary>
    public bool Release()
    {
        var was = Dragging;
        Pressed = false;
        Dragging = false;
        _moved = Vector2.Zero;
        return was;
    }
}
