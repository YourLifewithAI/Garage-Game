using System.Collections.Generic;
using System.Globalization;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// Spending Fractals at will (docs/game/MECHANICS.md section 6 and decision 41, the founder on 15 October): the player's own
/// fractal.spend, with no key of its own. The camp's screen (sitting by a campfire, decision 87) and the journal's Character page show the
/// Fractals in hand and, at the camp, two buttons,
/// "A new heart" and "The next band", each with its price; away from the camp it says to go back; a short hint points to the
/// journal when the player stands at the camp with enough in hand. Everything here is read from the host (its rules, the body, the
/// camp) and the one thing it does is the player's command through the host's one path (CommandHost.PlayerSpendFractals); the
/// host decides, and its refusals, written for a child, are shown as they come. The companion's connection never reaches any of
/// this: the host refuses the command to any principal but the player's. The words follow the pack's names block (Fractals,
/// hearts, stamina, camp).
/// </summary>
public static class FractalSpend
{
    /// <summary>The ids of the page's two buttons: what a button line's Target says.</summary>
    public const string HeartTarget = "spend:heart";
    public const string BandTarget = "spend:band";
    public const string HeartGrow = "heart";
    public const string BandGrow = "band";

    /// <summary>What one kind of growth stands at for the player now: its price, the Fractals in hand for it, whether it is at its cap.</summary>
    public readonly record struct Offer(string Grow, int Price, int Held, bool Capped)
    {
        /// <summary>Enough in hand, and room to grow. (The camp is a place, not an offer.)</summary>
        public bool Affordable => !Capped && Held >= Price;
    }

    /// <summary>
    /// The offer for "heart" or "band", or null where the island has none: a pack with no Fractals, or hearts or a bar that do not
    /// grow. Allocation-free, so the HUD may ask it often.
    /// </summary>
    public static Offer? OfferFor(CommandHost host, string grow)
    {
        var rules = host.BodyRules;
        if (rules.FractalRules is not { } fractals) return null;
        var heart = grow == HeartGrow;
        if (heart ? rules.Hearts == null : rules.Stamina == null) return null;
        var vitals = host.Vitals;
        var capped = heart ? rules.MaxHearts(vitals) >= rules.Hearts!.CapHearts : rules.OpenBands(vitals) >= rules.Stamina!.Bands;
        return new Offer(grow, heart ? fractals.PerHeart : fractals.PerBand, vitals.Fractals.InHand(grow, fractals.Typed), capped);
    }

    /// <summary>Whether the Fractals in hand pay for a heart or a band that can still grow (wherever the player stands).</summary>
    public static bool CanAfford(CommandHost host) =>
        OfferFor(host, HeartGrow) is { Affordable: true } || OfferFor(host, BandGrow) is { Affordable: true };

    /// <summary>The short hint: the Fractals in hand, and where to spend them (the camp's screen when you sit, decision 87, or the journal).</summary>
    public static string HintWords(CommandHost host, string journalKey, string sitKey) =>
        $"You have {host.Vitals.Fractals.Unspent} {MeterWords.Name(host, "fractals")}! Sit by the campfire ({sitKey}) to spend them on a heart or the next band, or open the journal ({journalKey}) at {JournalPages.Character}.";

    /// <summary>
    /// The Fractals section of the Character page: what is in hand and a picture of the way to the next spend, then at the camp
    /// the two buttons (dimmed until the Fractals in hand pay for them), away from it where to go. Nothing for a pack with no
    /// Fractals. A Fractal's whereabouts is never said.
    /// </summary>
    public static List<JournalLine> Lines(CommandHost host)
    {
        var lines = new List<JournalLine>();
        var rules = host.BodyRules;
        if (rules.FractalRules is not { } fractals) return lines;
        var word = MeterWords.Name(host, "fractals");
        var camp = MeterWords.Name(host, "camp").ToLowerInvariant();
        var heart = OfferFor(host, HeartGrow);
        var band = OfferFor(host, BandGrow);
        var held = host.Vitals.Fractals.Unspent;
        lines.Add(new JournalLine(JournalInk.Heading, word));
        // The nearest spend still to be had: its price, for "4 of 5".
        var open = new List<Offer>(2);
        if (heart is { Capped: false } h) open.Add(h);
        if (band is { Capped: false } b) open.Add(b);
        if (open.Count == 0)
            lines.Add(new JournalLine(JournalInk.Text, $"You have {held} {word}. There is nothing more for them to grow here: every heart and every band is yours."));
        else
        {
            var next = open.Count == 2 ? System.Math.Min(open[0].Price, open[1].Price) : open[0].Price;
            var grows = string.Join(" or ", open.ConvertAll(o => o.Grow == HeartGrow ? "a heart" : $"the next band of your {MeterWords.Name(host, "stamina").ToLowerInvariant()}"));
            lines.Add(new JournalLine(JournalInk.Text, held == 0
                ? $"You have no {word} yet. {next} of them can grow {grows}."
                : held < next ? $"You have {held} {word}: {held} of {next} toward your next spend."
                : $"You have {held} {word}: enough for something new!"));
        }
        if (!host.PlayerAtCamp())
        {
            lines.Add(new JournalLine(JournalInk.Text, $"{word} are spent at the {camp}: go back to where you started."));
            if (open.Count > 0)
                lines.Add(new JournalLine(JournalInk.Faint, string.Join(", ", Prices(heart, band, word, fractals.Typed)) + "."));
            return lines;
        }
        lines.Add(new JournalLine(JournalInk.Faint, $"You are at the {camp}: spend them here."));
        AddOffer(lines, heart, "A new heart", HeartTarget, word, fractals.Typed, "every heart this island allows");
        AddOffer(lines, band, "The next band", BandTarget, word, fractals.Typed, "every band this island allows");
        if (open.Count > 0 && heart is not { Affordable: true } && band is not { Affordable: true })
        {
            var need = int.MaxValue;
            foreach (var offer in open) need = System.Math.Min(need, offer.Price - offer.Held);
            lines.Add(new JournalLine(JournalInk.Faint, $"Find {need} more to spend them."));
        }
        return lines;
    }

    private static IEnumerable<string> Prices(Offer? heart, Offer? band, string word, bool typed)
    {
        if (heart is { Capped: false } h) yield return $"A new heart costs {Price(h.Price, "heart", word, typed)}";
        if (band is { Capped: false } b) yield return $"the next band {Price(b.Price, "band", word, typed)}";
    }

    private static string Price(int price, string kind, string word, bool typed) => $"{price} {(typed ? kind + " " : "")}{word}";

    private static void AddOffer(List<JournalLine> lines, Offer? offer, string name, string target, string word, bool typed, string capped)
    {
        if (offer is not { } o) return;
        if (o.Capped) lines.Add(new JournalLine(JournalInk.Faint, $"You have {capped}."));
        else lines.Add(new JournalLine(JournalInk.Button, $"{name} · {Price(o.Price, o.Grow, word, typed)}", target, o.Affordable));
    }

    /// <summary>
    /// The player's spend, as the host answers it: the words of the new heart or band, or the host's own refusal (written for a
    /// child: how many it takes and how many you have, or to go back to the camp). The host decides everything; nothing is checked
    /// here that could disagree with it.
    /// </summary>
    public static string Spend(CommandHost host, string grow)
    {
        var result = host.PlayerSpendFractals(grow);
        if (result["ok"]?.GetValue<bool>() != true) return result["error"]?["message"]?.GetValue<string>() ?? "That could not be spent.";
        var vitals = host.Vitals;
        return grow == HeartGrow
            ? $"A new heart! You have {host.BodyRules.MaxHearts(vitals)} {MeterWords.Name(host, "hearts").ToLowerInvariant()} now."
            : $"The next band is open! Your {MeterWords.Name(host, "stamina").ToLowerInvariant()} is now {vitals.MaxStamina.ToString("0.0#", CultureInfo.InvariantCulture)} m.";
    }
}
