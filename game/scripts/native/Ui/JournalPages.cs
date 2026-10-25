using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>How a line of the journal is written: the size and ink of its words, or a link, a button, or the map.</summary>
public enum JournalInk { Heading, Title, Text, Faint, Smudge, Link, Map, Button }

/// <summary>
/// One line of a journal page. A link's Target is the page it opens; a button's Target is the action it asks the HUD for
/// (FractalSpend.HeartTarget), and a button that is not Enabled is drawn dimmed and does nothing.
/// </summary>
public readonly record struct JournalLine(JournalInk Ink, string Text, string Target = "", bool Enabled = true);

/// <summary>A page as written now: its lines, whether a further leaf follows, and the line at its foot.</summary>
public sealed record JournalPage(IReadOnlyList<JournalLine> Lines, bool HasMore, string Footer);

/// <summary>
/// What each page of the journal says, written from the host's data alone and never from scene nodes: the Glow progress the
/// host verified (per mode), the team's discovered map, the loaded island's rules, the player's status and the journal's own
/// history and notes. Every key it names comes from the input map. The words of the Glow discoveries and the Gubble's hints are
/// curated here (they moved from the retired Glow journal panel); a command's arguments, a world's names and the AI's notes
/// never write them or unlock anything. A discovery not yet made stays an unreadable smudge: neither its name nor its trigger.
/// </summary>
public static class JournalPages
{
    public const string Index = "Index";
    public const string Discoveries = "Discoveries";
    public const string Abilities = "Abilities";
    public const string Character = "Character";
    public const string Map = "Map";
    public const string Notes = "Notes & memories";
    public static readonly string[] Sections = { Discoveries, Abilities, Character, Map, Notes };
    private static readonly string[] SectionWords =
    {
        "Magic, places and things we have found", "Magic and the ways we move", "Your character sheet, and Fractals to spend",
        "The world we know together", "Thoughts and things worth keeping",
    };
    /// <summary>Entries on one leaf of a long page (Places & things, memories).</summary>
    public const int PerLeaf = 12;
    /// <summary>The unreadable ink of a discovery still to come: the same for each, so its length gives nothing away.</summary>
    public const string Smudge = "~~~~ ~~~ ~~~~~~ ~~ ~~~~";

    /// <summary>
    /// The Glow discoveries' words, in the host's discovery order. Constellation has a second form once five lights are learned:
    /// the acknowledgement that its points and links can now be changed (RoomHud.MagicGlow.cs shows those controls then).
    /// </summary>
    private static readonly (string Id, string Title, string Words)[] GlowDiscoveries =
    {
        (GlowProgress.Lantern, "Lantern", "A light that travels with the Gubble."),
        (GlowProgress.Torch, "Torch", "A bright light resting on a high place."),
        (GlowProgress.Bioluminesce, "Bioluminesce", "Tiny living-looking lights gather across the water."),
        (GlowProgress.Constellation, "Constellation", "Nearby lights connect into a small pattern."),
        (GlowProgress.Skylight, "Skylight", "A small star shining above the island."),
    };
    /// <summary>Fire's line on the Abilities page while it is locked (decision 72).</summary>
    public const string FireLocked = "Locked: the Gubble learns Fire when you first rest by the starting campfire.";
    private const string ConstellationUnlocked = "Lights joined into your own pattern. You can now move its points and change their links.";

    /// <summary>The Gubble's hints, curated, each shown only once the host delivered it and only until its discovery is made.</summary>
    private static readonly (string Hint, string Discovery, string Words)[] GlowHints =
    {
        (GlowProgress.LanternHint, GlowProgress.Lantern, "Try asking me to glow on myself."),
        ("torch_high_place", GlowProgress.Torch, "I wonder how a light would feel in a high place."),
        ("bioluminesce_water", GlowProgress.Bioluminesce, "The water might have its own way to shine."),
        ("constellation_nearby", GlowProgress.Constellation, "Maybe a few nearby lights could make a pattern."),
        ("skylight_sky", GlowProgress.Skylight, "Could we try a light above us, under the open sky?"),
    };

    /// <summary>
    /// The plain names of the other abilities' expertise, for a learned one to be listed by (M11): no words, no hints and no smudges
    /// for Bubbles while its list is not final. Grow's list (decision 75) and Fire's (decisions 77 to 80) are the founder's: their unlearned
    /// entries are smudges and their delivered hints show, as Glow's (GrowHints, FireHints).
    /// </summary>
    private static readonly (string Ability, string Name, (string Id, string Title)[] Entries)[] OtherExpertise =
    {
        (ExpertiseLists.Bubbles, "Bubbles", new[]
        {
            (ExpertiseLists.BubbleCarry, "Bubble carry"), (ExpertiseLists.BubbleBoat, "Bubble boat"), (ExpertiseLists.BubbleLift, "Bubble lift"),
        }),
        (ExpertiseLists.Fire, "Fire", new[]
        {
            (ExpertiseLists.FirstFire, "Fire"), (ExpertiseLists.Campfire, "Campfire"), (ExpertiseLists.Fireline, "Fireline"),
            (ExpertiseLists.Snuff, "Snuff"), (ExpertiseLists.WillOWisp, "Will-o'-wisp"),
        }),
        (ExpertiseLists.Grow, "Grow", new[]
        {
            (ExpertiseLists.TallPlant, "Tall plant"), (ExpertiseLists.Thicket, "Thicket"), (ExpertiseLists.GreenAsh, "Green ash"),
            (ExpertiseLists.RainGarden, "Rain garden"), (ExpertiseLists.Waterpad, "Waterpad"),
        }),
    };

    /// <summary>Grow's hints (ExpertiseLists.GrowHintOrder), each pointing at its expertise without naming the trigger, shown once delivered until it is learned.</summary>
    private static readonly (string Hint, string Discovery, string Words)[] GrowHints =
    {
        ("tall_plant_one", ExpertiseLists.TallPlant, "I wonder how big one plant could get."),
        ("thicket_together", ExpertiseLists.Thicket, "What if a few plants grew up tall together?"),
        ("green_ash_burnt", ExpertiseLists.GreenAsh, "The burnt ground looks so sad. Could Grow help it?"),
        ("rain_garden_rain", ExpertiseLists.RainGarden, "Plants love the rain. I wonder what Grow does in it."),
        ("waterpad_water", ExpertiseLists.Waterpad, "Could something grow on the water?"),
    };

    /// <summary>Fire's hints (ExpertiseLists.FireHintOrder), each pointing at its expertise without naming the trigger, shown once delivered until it is learned.</summary>
    private static readonly (string Hint, string Discovery, string Words)[] FireHints =
    {
        ("first_fire_rest", ExpertiseLists.FirstFire, "That campfire looks so cosy. Shall we stop there a while?"),
        ("campfire_firepits", ExpertiseLists.Campfire, "Those cold old rings of stones... I wonder if they miss being warm."),
        ("fireline_row", ExpertiseLists.Fireline, "What if a few little fires stood in a row?"),
        ("snuff_wet", ExpertiseLists.Snuff, "Fires really don't like getting wet, do they?"),
        ("wisp_water", ExpertiseLists.WillOWisp, "Could a flame ever float on water?"),
    };

    /// <summary>
    /// An expertise's name as the journal writes it (the expertise announcement's card reads it too): Glow's discoveries and the other
    /// abilities' lists; the id itself for one no list names.
    /// </summary>
    public static string ExpertiseTitle(string ability, string id)
    {
        if (ability == ExpertiseLists.Glow)
            foreach (var (glowId, title, _) in GlowDiscoveries)
                if (glowId == id) return title;
        foreach (var (other, _, entries) in OtherExpertise)
            if (other == ability)
                foreach (var (entryId, title) in entries)
                    if (entryId == id) return title;
        return id;
    }

    /// <summary>A page as the host's data writes it now. leaf counts PerLeaf entries from the start of a long page.</summary>
    public static JournalPage Build(string page, CommandHost? host, int leaf)
    {
        if (page == Index) return IndexPage(host);
        if (host == null || !GodotObject.IsInstanceValid(host)) return new(new[] { Text("The journal will be ready when the island is ready.") }, false, "");
        return page switch
        {
            Discoveries => DiscoveriesPage(host, leaf),
            Abilities => AbilitiesPage(host),
            Character => CharacterPage(host),
            Map => MapPage(host),
            _ => NotesPage(host, leaf),
        };
    }

    private static JournalLine Heading(string text) => new(JournalInk.Heading, text);
    private static JournalLine Title(string text) => new(JournalInk.Title, text);
    private static JournalLine Text(string text) => new(JournalInk.Text, text);
    private static JournalLine Faint(string text) => new(JournalInk.Faint, text);
    private static string K(StringName action) => PlayerControls.Label(action);
    /// <summary>A name from the world (untrusted): plain display text, shown literally.</summary>
    private static string Name(JsonNode? node, int length) => KernelJson.DisplayText(node?.GetValue<string>() ?? "", length).Trim() is { Length: > 0 } name ? name : "Something";
    private static string CompanionName(CommandHost host) =>
        host.Companion is { } companion && GodotObject.IsInstanceValid(companion) ? KernelJson.DisplayText(companion.CompanionName, 40) : "the Gubble";
    private static string ModeWords(string mode) => mode switch { "challenge" => "Challenge", "live" => "Live", _ => "Creative" };

    /// <summary>
    /// What a page is called on screen: its id, except the Discoveries page, which reads the pack's word for expertise (the names
    /// block, MECHANICS decision J: the founder's word for what the code calls discoveries; today's word without one). The ids
    /// never change: they name the page in code and in the index's links.
    /// </summary>
    public static string Title(string page, CommandHost? host) => page == Discoveries ? MeterWords.Name(host, "expertise") : page;

    private static JournalPage IndexPage(CommandHost? host)
    {
        var lines = new List<JournalLine> { Text("Where shall we turn?") };
        for (var i = 0; i < Sections.Length; i++)
        {
            lines.Add(new JournalLine(JournalInk.Link, $"{i + 1:00}   {Title(Sections[i], host)}", Sections[i]));
            lines.Add(Faint(Sections[i] == Character ? $"Your character sheet, and {MeterWords.Name(host, "fractals")} to spend" : SectionWords[i]));
        }
        return new(lines, false, "Written together, a little at a time.");
    }

    // ---- Discoveries: the magic we found (Glow progress), then places and things (the discovered map) ----

    private static JournalPage DiscoveriesPage(CommandHost host, int leaf)
    {
        var lines = new List<JournalLine>();
        var discovered = host.ExportDiscovered();
        if (leaf == 0)
        {
            lines.Add(Heading("Magic we found"));
            lines.AddRange(GlowLines(host));
            lines.AddRange(OtherLines(host));
        }
        lines.Add(Heading("Places & things"));
        if (leaf == 0) lines.Add(Faint(GroundWords(discovered)));
        var found = discovered["entities"]!.AsObject().Select(p => p.Value!)
            .OrderByDescending(e => e["last_seen_utc"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
        if (found.Count == 0) lines.Add(Text("A fresh world. Explore with the Gubble to fill these pages."));
        foreach (var known in found.Skip(leaf).Take(PerLeaf))
        {
            var entity = known["entity"]!;
            lines.Add(Title(Name(entity["display_name"], 60)));
            var stale = known["may_be_stale"]?.GetValue<bool>() == true ? " · it may have changed since" : "";
            lines.Add(Faint((entity["category"] is { } category ? Name(category, 40) : "A discovery") + " · last seen together" + stale));
        }
        return new(lines, leaf + PerLeaf < found.Count, "Our shared knowledge, remembered as last seen.");
    }

    /// <summary>
    /// The Glow page's words (the retired Glow journal's): how many lights this island lets Glow hold, each discovery the host
    /// recorded in this mode, a smudge for each still to come, the hints delivered for those, and the host's notice if the
    /// progress could not be read or saved. No use counts or hidden thresholds.
    /// </summary>
    public static IEnumerable<JournalLine> GlowLines(CommandHost host)
    {
        var limit = host.EffectiveEffectLimit("glow");
        yield return Text(limit > 0 ? $"Glow can hold {limit} {(limit == 1 ? "light" : "lights")} at a time." : "Glow is not available on this island.");
        yield return Faint("What we learned together; it comes with us into every mode.");
        var progress = host.GlowProgress;
        if (progress.Discoveries.Count == 0) yield return Text($"Your Glow {MeterWords.Name(host, "expertise").ToLowerInvariant()} will appear here.");
        foreach (var (id, title, words) in GlowDiscoveries)
        {
            if (!progress.HasDiscovery(id)) continue;
            yield return Title(title);
            yield return Text(id == GlowProgress.Constellation && progress.ActiveLightAllowance >= 5 ? ConstellationUnlocked : words);
        }
        if (limit > 0)
            foreach (var _ in GlowDiscoveries.Where(d => !progress.HasDiscovery(d.Id)))
                yield return new JournalLine(JournalInk.Smudge, Smudge);
        // Only hints the host has already delivered appear, and only until their discovery is made.
        foreach (var (hint, discovery, words) in GlowHints)
        {
            if (!progress.DeliveredHints.Contains(hint, StringComparer.Ordinal) || progress.HasDiscovery(discovery)) continue;
            yield return Faint("A thought from the Gubble");
            yield return Text(words);
        }
        if (host.GlowProgressNotice.Length > 0) yield return Text(host.GlowProgressNotice);
    }

    /// <summary>
    /// The expertise the pair learned in the other abilities, each by its plain name under its ability's name, and nothing for an
    /// ability with none learned: no words, no use counts and no smudge for what is still to come. The host's own record.
    /// </summary>
    public static IEnumerable<JournalLine> OtherLines(CommandHost host)
    {
        foreach (var (ability, name, entries) in OtherExpertise)
        {
            var record = host.GlowProgress.Of(ability);
            // Grow's list (decision 75) and Fire's (77 to 80) are final: shown on an island that grants the ability even before anything is
            // learned, with smudges and hints.
            var final = (ability == ExpertiseLists.Grow && host.Rules?.Ability("grow") != null) || (ability == ExpertiseLists.Fire && host.Rules?.Ability("fire") != null);
            if (!final && record is not { Discoveries.Count: > 0 }) continue;
            yield return Title(name);
            foreach (var (id, title) in entries)
                if (record?.Has(id) == true) yield return Text(title);
            // What Grow's expertise has opened (decision 76): the unlocks found, by name.
            if (ability == ExpertiseLists.Grow && GrowLocks.FoundNames(host).ToList() is { Count: > 0 } found)
            {
                yield return Faint("What they opened");
                foreach (var found1 in found) yield return Text(found1);
            }
            if (!final) continue;
            foreach (var _ in entries.Where(e => record?.Has(e.Id) != true)) yield return new JournalLine(JournalInk.Smudge, Smudge);
            foreach (var (hint, discovery, words) in ability == ExpertiseLists.Fire ? FireHints : GrowHints)
            {
                if (record == null || !record.DeliveredHints.Contains(hint, StringComparer.Ordinal) || record.Has(discovery)) continue;
                yield return Faint("A thought from the Gubble");
                yield return Text(words);
            }
        }
    }

    /// <summary>How much ground the team has looked over: the discovered cells of every level.</summary>
    private static string GroundWords(JsonObject discovered)
    {
        var cell = discovered["cell_m"]!.GetValue<double>();
        var levels = discovered["levels"]!.AsArray().OfType<JsonObject>().ToList();
        var cells = levels.Sum(level => Convert.FromBase64String(level["cells"]!.GetValue<string>()).Sum(b => System.Numerics.BitOperations.PopCount(b)));
        if (cells == 0) return "No ground explored yet.";
        var area = cells * cell * cell;
        var where = levels.Count == 1 ? "one surface" : $"{levels.Count} surfaces";
        return area < 1 ? $"We have looked over about {area * 10000:0} square centimetres, on {where}." : $"We have looked over about {area:0.0} square metres, on {where}.";
    }

    // ---- Abilities: the island's rules pack, what has been learned, and the keys from the input map ----

    private static JournalPage AbilitiesPage(CommandHost host)
    {
        var lines = new List<JournalLine> { Heading($"{CompanionName(host)}'s magic here") };
        lines.AddRange(AbilityLines(host));
        lines.Add(Heading("Asking the Gubble"));
        lines.Add(Text($"{K(Act.GubbleAsk)}: ask for what fits where you point · hold it for the magic wheel"));
        lines.Add(Text($"{K(Act.GubbleAsk)} on a bubble chooses it, then on a spot leads it there · a higher spot lifts it · {K(Act.MouseRelease)} lets it go"));
        lines.Add(Text($"{K(Act.GubbleRecall)} come, then follow · {K(Act.GubbleStop)} stop"));
        lines.Add(Text($"{K(Act.GubbleEyes)} or the eye icon: see through {CompanionName(host)}'s eyes"));
        lines.Add(Heading("Ways through the world"));
        lines.Add(Text($"{PlayerControls.MoveLabel()} walk · {K(Act.MoveSprint)} run · {K(Act.Jump)} jump"));
        lines.Add(Text($"Climb: keep walking into a steep face · {K(Act.Jump)} lets go"));
        lines.Add(Text($"Swim: deep water floats you · Dive: hold {K(Act.MoveDive)}, {K(Act.Jump)} rises"));
        lines.Add(Text($"{K(Act.Hands)} pick up and put down · {K(Act.Push)} push"));
        var home = host.Player is { } body && GodotObject.IsInstanceValid(body) ? body.HomeName : "the start";
        lines.Add(Text($"{K(Act.BodyHome)} back to {home} · {K(Act.BodyRecover)} recover"));
        return new(lines, false, "Keys as your controls have them now.");
    }

    /// <summary>
    /// The island's abilities as the Abilities page lists them (the camp's Magic page reads the same): each by its key and name, what it
    /// does, Fire's lock until the first rest, its reach and limit, and its price to the player. The island's rules pack and the host only.
    /// </summary>
    public static IEnumerable<JournalLine> AbilityLines(CommandHost host)
    {
        if (host.Rules is not { } rules)
        {
            yield return Text("This island's magic is not ready yet.");
            yield break;
        }
        foreach (var ability in rules.Abilities)
        {
            var slot = Array.IndexOf(GubbleMagic.SlotCategories, ability.Category);
            var name = KernelJson.DisplayText(ability.DisplayName, 24);
            yield return Title(slot >= 0 && slot < Act.GubbleSlots.Length ? $"{K(Act.GubbleSlots[slot])} · {name}" : $"{name} · in the Magic panel");
            yield return Text(AbilityWords(ability));
            // Decision 72: Fire is listed, locked, until the first rest by the starting campfire teaches it.
            if (ability.Primitive == "element.ignite" && !host.FireLearned) yield return Faint(FireLocked);
            var limit = host.EffectiveEffectLimit(ability.Capability);
            yield return Faint($"Reaches {Distance(ability.ReachM)} from the Gubble · {(limit == 1 ? "one at a time" : $"up to {limit} at once")}");
            // What it costs you in pips (the ordered price: the player's order, never a field).
            if (host.PriceFor(ability.Capability, CommandHost.PlayerPrincipal) is > 0 and var cost)
                yield return Faint($"Costs {cost} {MeterWords.Counted(host, "pips", cost)}");
            if (ability.Category == "light" && host.GlowProgress.Discoveries.Count > 0)
                yield return Faint("Learned together: " + string.Join(", ", GlowDiscoveries.Where(d => host.GlowProgress.HasDiscovery(d.Id)).Select(d => d.Title)));
        }
    }

    /// <summary>What an ability does, from its engine primitive and where it may land (its targets): only what the game does.</summary>
    private static string AbilityWords(IslandAbility ability)
    {
        // The Bubble (decisions 55 to 60): one bubble at a spot, the place deciding what it is, steered by the smart ask.
        if (ability.Primitive == "particles.float")
            return $"One bubble at a spot, and the place decides: a ride round you, a boat on water, a bubble to step into anywhere else. {K(Act.GubbleAsk)} it to choose it, then a spot to lead it there; lead it higher to lift.";
        var what = ability.Primitive switch
        {
            "light.emit" => "A small light",
            "particles.burst" => "A burst of fireworks",
            "growth.seed" => "Flowers that take root",
            // Decisions 82 and 83: lit at once on the click, and burning until it burns itself out or Snuff puts it out (X stops the Gubble, never a fire).
            "element.ignite" => "A fire lit at once, inside its outlined circle, that burns until it burns itself out or Snuff puts it out",
            _ => "Magic",
        };
        var point = ability.Targets.Contains("point");
        var self = ability.Targets.Contains("self");
        var where = point && self ? "where you point, or around the Gubble itself" : point ? "where you point" : "around the Gubble itself";
        return $"{what}, {where}.";
    }

    private static string Distance(double metres) => metres < 1 ? $"{metres * 100:0} cm" : $"{metres.ToString("0.#", CultureInfo.InvariantCulture)} m";

    // ---- Character: the player's real values ----

    private static JournalPage CharacterPage(CommandHost host)
    {
        var lines = new List<JournalLine> { Heading("The small adventurer"), Text("A thumb-sized explorer, in a world made from your room.") };
        if (host.Player is { } body && GodotObject.IsInstanceValid(body))
        {
            lines.Add(Text($"Height {body.BodyHeightM * 100:0} cm · reach {body.ReachM * 100:0} cm"));
            lines.Add(Text($"Walk {body.WalkSpeedMps * 100:0} cm/s · run {body.RunSpeedMps * 100:0} cm/s"));
            lines.Add(Text($"Jump {body.JumpApexNowM * 100:0.#} cm · climb {body.ClimbSpeedMps * 100:0} cm/s"));
        }
        // The Fractals in hand and, at the camp, the two ways to spend them (decision 41: the player's own command, no key of its own).
        // High on the page, so the buttons are in view when the book opens on it.
        lines.AddRange(FractalSpend.Lines(host));
        // A held island (decision 54) is paused: the page says so in place of describing the mode the host happens to play (Creative),
        // its pool and its wallet.
        var held = host.IslandHeld;
        if (held)
        {
            lines.Add(Heading(IslandPaused.Heading));
            lines.Add(Text(IslandPaused.Words(host)));
        }
        // The read-only player.status query: the mode and this mode's vitals, as the host keeps them.
        var status = held ? null : host.PlayerQuery("player.status")["data"] as JsonObject;
        if (status != null)
        {
            var mode = status["mode"]?.GetValue<string>() ?? "creative";
            lines.Add(Heading($"{ModeWords(mode)} mode"));
            if (status["consequences"]?.GetValue<bool>() != true)
                lines.Add(Text($"Explore freely: climbing uses no {MeterWords.Lower(host, "stamina")}, and falls do not hurt you."));
            else
            {
                var stamina = Number(status["stamina"]);
                var maxStamina = Number(status["max_stamina"]);
                var share = maxStamina > 0 ? Math.Clamp(stamina / maxStamina * 100, 0, 100) : 0;
                lines.Add(Text($"Health {Number(status["health"]):0}/{Number(status["max_health"]):0}"));
                lines.Add(Text($"{MeterWords.Name(host, "stamina")} {share:0}% · climbing capacity {maxStamina:0.00} m"));
                lines.Add(Text($"Climbs completed {Number(status["completed_climbs"]):0} · recoveries {Number(status["recoveries"]):0}"));
            }
        }
        if (status != null)
        {
            // The pool keeps its numbers here, as the sheet always has; the logs and stones are the stacks (the wallet is retired, P-Inventory-2).
            if (status["energy"] is JsonObject energy)
                lines.Add(Text($"{MeterWords.Name(host, "pips")} {Number(energy["pips"]):0}/{Number(energy["max_pips"]):0}" +
                    (energy["shadow"]?.GetValue<bool>() == true ? " · a shadow pool here: it drains and refills, and never says no" : "")));
            if (status["pockets"] is JsonObject) lines.Add(Text("Logs and stones: " + MaterialWords(status)));
        }
        var carried = host.HeldName(CommandHost.PlayerAvatar) ?? "";
        lines.Add(Text("Carrying: " + (carried.Length == 0 ? "empty hands" : KernelJson.DisplayText(carried, 60))));
        lines.Add(Heading("Your companion"));
        lines.Add(Title(CompanionName(host)));
        lines.Add(Text("Floats after you, over water and up cliffs, and fetches the things you find."));
        // Trust in words, never a number (M8): the host's count from what happened, the pack's word for Trust above it.
        lines.Add(Faint(MeterWords.Name(host, "trust")));
        lines.Add(Text(host.TrustWords));
        return new(lines, false, "");
    }

    /// <summary>
    /// The logs and stones as the sheet writes them, from player.status: each stack of a kind with a material, in the pockets and the chest
    /// ("3 logs in your pockets · 1 stone in the chest"), or "none yet". Names are the host's display text.
    /// </summary>
    private static string MaterialWords(JsonObject status)
    {
        var parts = new List<string>();
        foreach (var (key, where) in new[] { ("pockets", "in your pockets"), ("chest", "in the chest") })
            if (status[key]?["entries"] is JsonArray entries)
                foreach (var entry in entries.OfType<JsonObject>().Where(e => e["material"] != null && e["kind"]?.GetValue<string>() == "stack"))
                    parts.Add($"{Number(entry["count"]):0} {entry["name"]?.GetValue<string>().ToLowerInvariant()} {where}");
        return parts.Count == 0 ? "none yet" : string.Join(" · ", parts);
    }

    /// <summary>A JSON number, whatever type wrote it (the host writes floats and ints).</summary>
    private static double Number(JsonNode? node) => node == null ? 0 : double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    // ---- Map: the team's discovered map ----

    private static JournalPage MapPage(CommandHost host) => new(new List<JournalLine>
    {
        Faint("Only the ground our eyes have found."),
        new(JournalInk.Map, ""),
        Faint($"● You     ◇ {CompanionName(host)}"),
        Faint("Darker ink is your level. Other heights fade. Blank parchment is still unexplored."),
    }, false, "");

    // ---- Notes & memories: the journal's history and the player's notes ----

    private static JournalPage NotesPage(CommandHost host, int leaf)
    {
        var lines = new List<JournalLine>();
        var journal = host.ExportJournal();
        // Selected memories, not a transcript: routine task completions (repeated fetches among them) stay off the page. The
        // Gubble's own notes are its AI's words; until they have a curated path to the player (docs/magic/README.md, curated
        // Gubble content first) they stay in the journal for the Gubble and are not shown here.
        var memories = journal["history"]!.AsArray().OfType<JsonObject>().Where(e => e["kind"]?.GetValue<string>() is "built" or "changed" or "removed" or "found")
            .Concat(journal["notes"]!.AsArray().OfType<JsonObject>().Where(e => e["author"]?.GetValue<string>() == CommandHost.PlayerPrincipal))
            .OrderByDescending(e => e["at_utc"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
        if (leaf == 0)
            foreach (var task in journal["open_tasks"]!.AsArray().OfType<JsonObject>().Where(e => e["line"]?.GetValue<string>().StartsWith("Fetching", StringComparison.Ordinal) == true).Take(3))
                lines.Add(Faint("Underway · " + task["line"]!.GetValue<string>()));
        if (memories.Count == 0) lines.Add(Text("These pages are for the things worth keeping: a creation, a discovery, a thought for another day."));
        foreach (var entry in memories.Skip(leaf).Take(PerLeaf))
        {
            var note = entry["kind"]?.GetValue<string>() == "note";
            lines.Add(Faint(note ? "Your note" : "A shared memory"));
            lines.Add(Text((note ? entry["text"] : entry["line"])?.GetValue<string>() ?? ""));
        }
        return new(lines, leaf + PerLeaf < memories.Count, "");
    }
}
