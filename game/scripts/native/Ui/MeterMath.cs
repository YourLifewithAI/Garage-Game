using System;
using System.Globalization;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// What the three meters say, worked out from the host's numbers and nothing else (docs/game/MECHANICS.md sections 1, 4, 5 and
/// 8): hearts in quarters, two rows of fifteen at most, shaking at one heart; the bar in rows of five bands; the pips and what an
/// ability costs in them, "needs 4" when the pool is short. Pure functions, so the headless suite can hold each rule by itself.
/// Every number here is the design's starting value; the pack's data decides the rest, and nothing is guessed past the design.
/// </summary>
public static class MeterMath
{
    public const int QuartersPerHeart = 4;
    /// <summary>A row of hearts holds this many; two rows is the most (30 hearts, the cap).</summary>
    public const int HeartsPerRow = 15;
    public const int MaxHeartRows = 2;
    /// <summary>The hearts shake while one heart or less is left (and some is): this many quarters.</summary>
    public const int ShakeAtQuarters = QuartersPerHeart;
    /// <summary>A price is drawn as this many dots at most; a larger one (a pack's own number) reads as a count.</summary>
    public const int MaxDots = 8;
    /// <summary>The glyph of one pip in words: the pips in their row and the price of an ability are the same dot.</summary>
    public const char PipGlyph = '●';
    /// <summary>The first bar's length in metres when the pack has no bands (the design's "2.0 m on the first bar").</summary>
    public const float LegacyRowM = 2.0f;

    /// <summary>The quarters of one heart that are full: 0 to 4. Hearts fill from the first, a heart's quarters clockwise from the top left.</summary>
    public static int FillOf(int quarters, int heart) => Math.Clamp(quarters - heart * QuartersPerHeart, 0, QuartersPerHeart);

    /// <summary>How many hearts are drawn for this many quarters at most (a part of a heart is a heart), never past the two rows.</summary>
    public static int HeartCount(int maxQuarters) => Math.Min(HeartsPerRow * MaxHeartRows, (Math.Max(0, maxQuarters) + QuartersPerHeart - 1) / QuartersPerHeart);

    public static int HeartRows(int hearts) => hearts <= 0 ? 0 : Math.Min(MaxHeartRows, (hearts + HeartsPerRow - 1) / HeartsPerRow);

    /// <summary>Whether the hearts shake: one heart or less left, and some (a knocked-out body wakes at camp). Never in Creative, where they are grey and full.</summary>
    public static bool Shakes(int quarters, bool grey) => !grey && quarters > 0 && quarters <= ShakeAtQuarters;

    /// <summary>The bar's two rows, in metres of bar: what the first row holds and what spills onto the second ("two rows past 2.0 m").</summary>
    public static (float First, float Second) BarRows(float maxM, float rowCapacityM)
    {
        var max = float.IsFinite(maxM) ? Math.Max(0, maxM) : 0;
        return rowCapacityM <= 0 ? (max, 0) : (Math.Min(max, rowCapacityM), Math.Max(0, max - rowCapacityM));
    }

    /// <summary>How much of each row is lit for this much stamina: the first row fills first, the second after it.</summary>
    public static (float First, float Second) BarFill(float staminaM, float firstM, float secondM)
    {
        var stamina = float.IsFinite(staminaM) ? Math.Clamp(staminaM, 0, firstM + secondM) : 0;
        return (Math.Min(stamina, firstM), Math.Max(0, stamina - firstM));
    }

    /// <summary>The length of the bar's first row: five bands of the pack's size (the Big bar knack makes each larger), or 2 m with no bands.</summary>
    public static float RowCapacityM(VitalsRules rules) =>
        rules.Stamina is { } stamina ? (float)(stamina.BandsPerBar * stamina.BandM * rules.StaminaPerBarFactor) : LegacyRowM;

    /// <summary>One band's length in metres (the ticks along the bar), 0 when the pack has no bands.</summary>
    public static float BandM(VitalsRules rules) => rules.Stamina is { } stamina ? (float)(stamina.BandM * rules.StaminaPerBarFactor) : 0;

    /// <summary>What an ability costs, drawn: its dots, or "needs N" while the pool cannot pay it. Nothing for a price of 0.</summary>
    public readonly record struct PriceTag(string Text, bool Needs)
    {
        public bool Shown => Text.Length > 0;
    }

    public static PriceTag Tag(int price, int balance)
    {
        if (price <= 0) return new PriceTag("", false);
        return balance >= price ? new PriceTag(Dots(price), false) : new PriceTag(Needs(price), true);
    }

    /// <summary>"needs 4": what the key hint and the wheel say while the pool is short of a price (the design's words).</summary>
    public static string Needs(int price) => $"needs {price}";

    /// <summary>A price in dots, one per pip; a very large one as a count so a hint stays short.</summary>
    public static string Dots(int price) => price <= MaxDots ? new string(PipGlyph, Math.Max(0, price)) : $"{PipGlyph}×{price}";

    // ---- the numbers option ----

    /// <summary>Hearts as the player counts them: whole hearts and the quarter ("3¼"), for the numbers option.</summary>
    public static string HeartsWords(int quarters)
    {
        var whole = quarters / QuartersPerHeart;
        var part = (quarters % QuartersPerHeart) switch { 1 => "¼", 2 => "½", 3 => "¾", _ => "" };
        return whole == 0 && part.Length > 0 ? part : whole.ToString(CultureInfo.InvariantCulture) + part;
    }

    public static string HeartsNumbers(string name, int quarters, int maxQuarters) =>
        $"{name} {HeartsWords(quarters)}/{HeartsWords(maxQuarters)}";

    public static string BarNumbers(string name, float staminaM, float maxM) =>
        string.Create(CultureInfo.InvariantCulture, $"{name} {staminaM:0.00}/{maxM:0.00} m");

    public static string PipsNumbers(string name, int pips, int maxPips) =>
        string.Create(CultureInfo.InvariantCulture, $"{name} {pips}/{maxPips}");

    /// <summary>A heart's outline in the unit square (+Y down): the classic curve, 36 points, closed by the drawer.</summary>
    public static Vector2[] HeartOutline()
    {
        const int points = 36;
        var raw = new Vector2[points];
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < points; i++)
        {
            var t = i * Mathf.Tau / points;
            var x = 16f * Mathf.Pow(Mathf.Sin(t), 3);
            var y = -(13f * Mathf.Cos(t) - 5f * Mathf.Cos(2 * t) - 2f * Mathf.Cos(3 * t) - Mathf.Cos(4 * t));
            raw[i] = new Vector2(x, y);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        for (var i = 0; i < points; i++) raw[i] = new Vector2((raw[i].X - minX) / (maxX - minX), (raw[i].Y - minY) / (maxY - minY));
        return raw;
    }
}
