using System;

namespace GarageGame.Native.Look.Intro;

/// <summary>Everything the intro shows at one moment: each part's progress from the room (0) to the island (1).</summary>
public readonly record struct IntroMoment(
    double Seconds, string Phase,
    float Cover, float Prompt, float Ceiling, float Land, float Coast, float Slump, float WallFray, float Sea, float Grow,
    bool LifeShown, bool BodiesShown, bool Done);

/// <summary>
/// The intro's story in about 15 seconds (the founder, 8 October: "It shows you the whole concept in the first 15-30 seconds. And people
/// will skip it later, but it creates a vibe. You're seeing creation happen in realtime"; with the island, the walls become the coast,
/// the ninth session). One continuous camera move (IntroCameraPath) from inside the room up to an overview, ending where play begins:
/// <list type="number">
/// <item><b>room</b>: the room roughly painted: the flat floor, the boxes, the walls and the ceiling, the sun only through the window
/// and the door;</item>
/// <item><b>sky</b>: the ceiling dissolves into the sky from its middle out, and the sun comes in;</item>
/// <item><b>land</b>: the boxes rise and soften into land, the ground round them after, the room's colours giving way to the land's;</item>
/// <item><b>coast</b>: the walls slump outward and down into the coast while the sea rises round the island;</item>
/// <item><b>water</b>: the sea comes up to its level, the ponds and streams fill;</item>
/// <item><b>plants</b>: the plants, trees, stones and cottages grow in, nearest the camera first;</item>
/// <item><b>arrive</b>: the camera comes down behind the player, the player and the Gubble appear, and control is handed over.</item>
/// </list>
/// Pure: the moment at any time, so a skip, a test or a capture can ask for any of them.
/// </summary>
public static class IntroTimeline
{
    public const double DurationS = 15.0;

    /// <summary>The phases in order, each with its window (seconds); windows overlap so one flows into the next.</summary>
    public static readonly (string Name, double Start, double End)[] Phases =
    {
        ("room", 0.0, 2.2), ("sky", 2.0, 4.6), ("land", 3.4, 8.6), ("coast", 6.0, 10.2), ("water", 9.0, 11.6), ("plants", 10.2, 13.2), ("arrive", 13.2, DurationS),
    };

    /// <summary>The black that hides the loading frames lifts over this.</summary>
    public const double CoverS = 0.8;
    /// <summary>The skip prompt shows from here, and fades before the end.</summary>
    public const double PromptFromS = 0.9, PromptUntilS = 14.0;
    /// <summary>The sea rises from the coast's start to the water's end.</summary>
    public const double SeaFromS = 6.4;
    /// <summary>The walls fray away over the last share of their slump.</summary>
    public const float WallFrayFrom = 0.55f;
    /// <summary>The player and the Gubble appear here, as the camera comes down to them.</summary>
    public const double BodiesAtS = 13.4;

    public static (double Start, double End) Window(string phase)
    {
        foreach (var (name, start, end) in Phases)
            if (name == phase) return (start, end);
        throw new ArgumentException($"no intro phase '{phase}'", nameof(phase));
    }

    /// <summary>The phase the story is in: the last one begun (a skip at that moment would skip it).</summary>
    public static string PhaseAt(double seconds)
    {
        var phase = Phases[0].Name;
        foreach (var (name, start, _) in Phases)
            if (seconds >= start) phase = name;
        return seconds >= DurationS ? "done" : phase;
    }

    /// <summary>The moment at a time: every part's progress, eased where it is not eased in the shaders.</summary>
    public static IntroMoment At(double seconds)
    {
        var t = Math.Max(0.0, seconds);
        var (skyStart, skyEnd) = Window("sky");
        var (landStart, landEnd) = Window("land");
        var (coastStart, coastEnd) = Window("coast");
        var (_, waterEnd) = Window("water");
        var (plantStart, plantEnd) = Window("plants");
        var slump = Smooth(Share(t, coastStart, coastEnd));
        return new IntroMoment(
            t, PhaseAt(t),
            Cover: 1f - Smooth(Share(t, 0.0, CoverS)),
            Prompt: Smooth(Share(t, PromptFromS, PromptFromS + 0.6)) * (1f - Smooth(Share(t, PromptUntilS, PromptUntilS + 0.6))),
            Ceiling: Smooth(Share(t, skyStart, skyEnd)),
            // The land's and the coast's own easing is per vertex, in the shader (each place has its moment).
            Land: Share(t, landStart, landEnd),
            Coast: Share(t, coastStart, coastEnd),
            Slump: slump,
            WallFray: Smooth(Math.Clamp((slump - WallFrayFrom) / (1f - WallFrayFrom), 0f, 1f)),
            // Smootherstep: the sea's last hand's breadth, where the ponds and streams fill, comes slowly at the end.
            Sea: Smoother(Share(t, SeaFromS, waterEnd)),
            Grow: Smooth(Share(t, plantStart, plantEnd)),
            LifeShown: t >= waterEnd,
            BodiesShown: t >= BodiesAtS,
            Done: t >= DurationS);
    }

    private static float Share(double t, double start, double end) => (float)Math.Clamp((t - start) / (end - start), 0.0, 1.0);

    private static float Smooth(float x) => x * x * (3f - 2f * x);

    private static float Smoother(float x) => x * x * x * (x * (x * 6f - 15f) + 10f);
}
