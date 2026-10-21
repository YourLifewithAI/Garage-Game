using System;

namespace GarageGame.Native;

/// <summary>
/// The dusk moment (RUN-2-GLOW.md section 4; the magic design's "Teaching at the moment of need"): the first time the light at the
/// player falls below the look's DarkThreshold while the player has never cast Glow, the Gubble shivers and dims and one hint
/// shows beside it. Ignored for RepeatAfterS, it shows once more; then it waits until it has been light and gets dark again.
/// Casting Glow once (the profile's has_used_glow) ends it for good. Glow is never locked: this only teaches it. The light is
/// read CheckEveryS (four times a second), never every frame.
/// </summary>
public sealed class DuskMoment
{
    public const double CheckEveryS = 0.25;
    /// <summary>How long a showing stays up (the hint, the dim).</summary>
    public const double ShowsForS = 8.0;
    /// <summary>A hint ignored this long, while it is still dark, shows once more.</summary>
    public const double RepeatAfterS = 20.0;

    public enum Step { None, Show, Hide }

    /// <summary>Whether the hint is up now.</summary>
    public bool Showing { get; private set; }
    /// <summary>Showings since it last got dark (0, 1 or 2).</summary>
    public int Shown { get; private set; }
    /// <summary>Every showing this session (tests).</summary>
    public int TotalShown { get; private set; }
    /// <summary>Times the light was read (tests: four times a second).</summary>
    public int Checks { get; private set; }
    /// <summary>Whether it was dark at the last reading.</summary>
    public bool Dark { get; private set; }

    private double _sinceCheck = CheckEveryS;
    private double _sinceShown;

    /// <summary>
    /// Time passes: lightLevel is read at most every CheckEveryS (and only while the dusk moment can still show), and usedGlow says
    /// whether Glow was ever cast. Returns what the HUD must do now.
    /// </summary>
    public Step Update(double delta, Func<float?> lightLevel, float darkThreshold, bool usedGlow)
    {
        if (usedGlow) return Stop();
        if (Shown > 0) _sinceShown += delta;
        _sinceCheck += delta;
        var step = Step.None;
        if (Showing && _sinceShown >= ShowsForS)
        {
            Showing = false;
            step = Step.Hide;
        }
        // (A hair under the period, so 15 frames at 60 fps are a quarter of a second despite the sum of fifteenths.)
        if (_sinceCheck < CheckEveryS - 1e-6) return step;
        _sinceCheck = 0;
        var level = lightLevel();
        Checks++;
        if (level is not { } light) return step;
        Dark = light < darkThreshold;
        if (!Dark)
        {
            // Light again: the next dark is a new dusk.
            if (!Showing) Shown = 0;
            return step;
        }
        // The first showing, and one more if ignored; after two, only a light reading (above) lets the next dark count.
        if (Showing) return step;
        if (Shown == 0 || (Shown == 1 && _sinceShown >= RepeatAfterS))
        {
            Shown++;
            TotalShown++;
            Showing = true;
            _sinceShown = 0;
            return Step.Show;
        }
        return step;
    }

    private Step Stop()
    {
        var was = Showing;
        Showing = false;
        return was ? Step.Hide : Step.None;
    }
}
