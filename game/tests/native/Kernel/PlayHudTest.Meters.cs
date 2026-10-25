using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Sandbox;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The three meters and the minimap (the mechanics round's packet M2, docs/game/MECHANICS.md sections 1, 4, 5, 6 and 8), in the
/// real room with the real HUD, headless. Hearts are drawn from the body's health in quarters, two rows of fifteen at most, and
/// shake at one heart; the bar sits under them in rows of five bands; in Creative both are full and grey. The pips are a row
/// under the bar, always (dots, spent ones rings, a shadow in Creative; decision 51); the price of an ability is what the PLAYER pays (by principal), as dots
/// on the wheel and the key hints and as "needs N" when the pool is short; an empty pool's refusal is the host's own words. The
/// numbers option is off until the player turns it on and lives in the profile. The minimap draws only what the host's
/// discovered map holds. The journal's Discoveries page reads the pack's word for expertise. The gather key sends item.gather.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>The price an ordered ability costs, drawn as the HUD draws it with the pool as it is now: dots, or "needs N" (the suite's own reading of the contract).</summary>
    private string PriceWords(string capability)
    {
        var host = CommandHost.Of(_world)!;
        var price = host.PriceFor(capability, CommandHost.PlayerPrincipal);
        var balance = Math.Max(0, host.Vitals.Pips);
        return price <= 0 ? "" : " " + (balance >= price ? new string('●', price) : $"needs {price}");
    }

    private static string Dots(int count) => new('●', count);

    /// <summary>A Glow as the player's command, the Gubble stopped after it (a stop costs nothing) so the room's limit on lights at once never refuses the next.</summary>
    private static bool CastGlow(CommandHost host, Vector3 spot)
    {
        var ok = host.PlayerEffect("glow", spot)["ok"]!.GetValue<bool>();
        host.PlayerGoal("stop");
        return ok;
    }

    /// <summary>An in-memory vitals store: the suite sets the body (many hearts, many bands) and the host reads it as it would a save.</summary>
    private sealed class MeterStore : IPlayerVitalsStore
    {
        private PlayerModesProfile _state;
        public MeterStore(PlayerModesProfile? profile = null) => _state = profile ?? PlayerModesProfile.Fresh;
        public PlayerModesProfile Load() => _state;
        public void Save(PlayerModesProfile profile) => _state = profile;
    }

    /// <summary>A body in Challenge with these Fractals spent (hearts bought, bands bought), its health at this share of the maximum.</summary>
    private static MeterStore BodyWith(int heartsBought, int bandsBought, float healthShare)
    {
        var fractals = new PlayerFractals(5 * (heartsBought + bandsBought), 0, heartsBought, bandsBought);
        var body = PlayerVitals.Of(100f * healthShare, 100f, 0.4f, 0.4f, Array.Empty<string>(), 0, fractals, PlayerVitals.UnsetPool);
        return new MeterStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(body));
    }

    /// <summary>Back to fresh bodies in every mode: full hearts, the starting bar, a full pool (what the suite's own tests expect).</summary>
    private static void FreshBodies(CommandHost host) => host.ConfigurePlayerVitals(new MeterStore());

    // ---- the rules of the meters, alone ----

    private void TestMeterMath()
    {
        // Hearts: a heart is four quarters; a part of a heart is a heart; two rows of fifteen at most.
        Check(MeterMath.FillOf(14, 0) == 4 && MeterMath.FillOf(14, 3) == 2 && MeterMath.FillOf(14, 4) == 0 && MeterMath.FillOf(1, 0) == 1 && MeterMath.FillOf(0, 0) == 0,
            "fourteen quarters fill three hearts and half of the fourth, in order, and none of the fifth");
        Check(MeterMath.HeartCount(20) == 5 && MeterMath.HeartCount(21) == 6 && MeterMath.HeartCount(120) == 30 && MeterMath.HeartCount(400) == 30 &&
              MeterMath.HeartRows(5) == 1 && MeterMath.HeartRows(15) == 1 && MeterMath.HeartRows(16) == 2 && MeterMath.HeartRows(30) == 2 && MeterMath.HeartRows(99) == 2,
            "five hearts are one row, sixteen are two, and thirty (the cap) are two rows of fifteen, never a third");
        Check(MeterMath.Shakes(4, false) && MeterMath.Shakes(1, false) && !MeterMath.Shakes(5, false) && !MeterMath.Shakes(0, false) && !MeterMath.Shakes(4, true),
            "the hearts shake at one heart left (four quarters or fewer), not above it, not with none, and never when grey");
        // A heart's picture: each quarter is red in its own quarter of the heart, clockwise from the top left, and nowhere else.
        var reds = Enumerable.Range(0, 5).Select(k => QuarterReds(MeterTextures.HeartPixels(k))).ToArray();
        Check(reds[0].SequenceEqual(new[] { 0, 0, 0, 0 }) && reds[1][0] > 40 && reds[1].Skip(1).All(n => n == 0) && reds[2][0] > 40 && reds[2][1] > 40 && reds[2][2] == 0 && reds[2][3] == 0 &&
              reds[3][0] > 40 && reds[3][1] > 40 && reds[3][2] > 40 && reds[3][3] == 0 && reds[4].All(n => n > 40),
            $"a heart with n quarters has n of its quarters red, clockwise from the top left: {string.Join(" | ", reds.Select(r => string.Join(",", r)))}");
        var grey = QuarterReds(MeterTextures.HeartPixels(4, grey: true));
        var greyPixels = MeterTextures.HeartPixels(4, grey: true);
        var anyColour = Enumerable.Range(0, greyPixels.Length / 4).Any(i => greyPixels[i * 4 + 3] > 200 && Math.Abs(greyPixels[i * 4] - greyPixels[i * 4 + 1]) > 30);
        Check(grey.All(n => n == 0) && !anyColour, "Creative's heart is full and grey: no red in it");
        // The bar: five bands to a row, a second row past 2.0 m, the first row fills first.
        var (first, second) = MeterMath.BarRows(0.4f, 2.0f);
        var (twoRows, spill) = MeterMath.BarRows(3.2f, 2.0f);
        var (exact, none) = MeterMath.BarRows(2.0f, 2.0f);
        Check(first == 0.4f && second == 0 && twoRows == 2.0f && Math.Abs(spill - 1.2f) < 1e-5f && exact == 2.0f && none == 0,
            "the bar is one row up to 2.0 m and a second row under it past that");
        var (litFirst, litSecond) = MeterMath.BarFill(2.5f, 2.0f, 1.2f);
        Check(litFirst == 2.0f && Math.Abs(litSecond - 0.5f) < 1e-5f && MeterMath.BarFill(1.0f, 2.0f, 1.2f) == (1.0f, 0f) && MeterMath.BarFill(-1f, 2f, 0f) == (0f, 0f),
            "the first row fills first, then the second, and stamina below nothing lights nothing");
        // Prices: dots, or "needs N" while the pool is short; nothing for no price.
        Check(MeterMath.Tag(4, 4) == new MeterMath.PriceTag(Dots(4), false) && MeterMath.Tag(4, 3) == new MeterMath.PriceTag("needs 4", true) &&
              MeterMath.Tag(2, 0).Text == "needs 2" && !MeterMath.Tag(0, 5).Shown && MeterMath.Dots(9) == "●×9",
            "a price reads as that many dots, or \"needs 4\" when the pool is a pip short, and a free ability says nothing");
        Check(MeterMath.HeartsWords(14) == "3½" && MeterMath.HeartsWords(13) == "3¼" && MeterMath.HeartsWords(15) == "3¾" && MeterMath.HeartsWords(1) == "¼" && MeterMath.HeartsWords(20) == "5" &&
              MeterMath.HeartsNumbers("Hearts", 14, 20) == "Hearts 3½/5" && MeterMath.BarNumbers("Stamina bar", 0.4f, 0.4f) == "Stamina bar 0.40/0.40 m" && MeterMath.PipsNumbers("Pips", 6, 8) == "Pips 6/8",
            "the numbers option counts hearts in whole hearts and quarters, the bar in metres and the pool in pips");
        // The meters in isolation: what each says about itself.
        var hearts = new HeartsMeter();
        hearts.Set(14, 120, false);
        Check(hearts.HeartCount == 30 && hearts.GetCombinedMinimumSize().Y > HeartsMeter.HeartPx * 2 && hearts.GetCombinedMinimumSize().X <= 15 * (HeartsMeter.HeartPx + HeartsMeter.GapPx) + 0.01f,
            $"thirty hearts are two rows of fifteen ({hearts.GetCombinedMinimumSize()})");
        hearts.Set(40, 40, true);
        Check(hearts.Grey && !hearts.Shaking && hearts.HeartCount == 10, "grey hearts are full and never shake");
        hearts.Free();
        var bar = new StaminaBar();
        bar.Set(3.0f, 4.0f, 2.0f, 0.4f, false, false);
        var oneRow = new StaminaBar();
        oneRow.Set(0.4f, 0.4f, 2.0f, 0.4f, false, false);
        Check(bar.RowCount == 2 && oneRow.RowCount == 1 && bar.GetCombinedMinimumSize().Y > oneRow.GetCombinedMinimumSize().Y &&
              Math.Abs(bar.GetCombinedMinimumSize().X - 2.0f * StaminaBar.PxPerM) < 0.01f,
            "a bar of 4.0 m is two rows of 2.0 m, a bar of 0.4 m is one short row");
        bar.Free();
        oneRow.Free();
        TestMeterWordsAlone();
    }

    /// <summary>The red pixels in each quarter of a heart's picture (top left, top right, bottom right, bottom left): opaque and clearly red.</summary>
    private static int[] QuarterReds(byte[] pixels)
    {
        var counts = new int[4];
        var side = MeterTextures.HeartPx;
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var at = (y * side + x) * 4;
                if (pixels[at + 3] < 200 || pixels[at] < 180 || pixels[at + 1] > 110) continue;
                counts[x < side / 2 ? (y < side / 2 ? 0 : 3) : (y < side / 2 ? 1 : 2)]++;
            }
        return counts;
    }

    private void TestMeterWordsAlone()
    {
        Check(MeterWords.Name(null, "expertise") == "Discoveries" && MeterWords.Name(null, "hearts") == "Hearts" && MeterWords.Name(null, "stamina") == "Stamina" &&
              JournalPages.Title(JournalPages.Discoveries, null) == "Discoveries" && JournalPages.Title(JournalPages.Abilities, null) == "Abilities",
            "without a pack's names block the words are today's: the Discoveries page is called Discoveries");
    }

    // ---- hearts and the bar, from the host's body ----

    private async Task TestHeartsAndBar()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        player.ReadKeyboard = false;
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(4);
        var box = (VBoxContainer)_hud.FindChild("Meters", true, false)!;
        Check(box.IsVisibleInTree() && _hud.Hearts.IsVisibleInTree() && _hud.Bar.IsVisibleInTree() && box.GetParent<Node>().GetParent<Node>() is PanelContainer,
            "the hearts and the bar are in the meters' panel at the upper left, always visible");
        Check(_hud.Hearts.Grey && _hud.Hearts.Quarters == 20 && _hud.Hearts.MaxQuarters == 20 && _hud.Hearts.HeartCount == 5 && !_hud.Hearts.Shaking &&
              _hud.Bar.Grey && _hud.Bar.StaminaM == _hud.Bar.MaxM && _hud.Bar.MaxM > 0 && !_hud.Bar.IsSlowed,
            "in Creative the hearts and the bar are drawn full and grey: nothing hurts or drains there");
        Check(!_hud.MeterNumbers && !_hud.HeartsNumbers.Visible && !_hud.BarNumbers.Visible && !RoomHud.LoadMeterNumbers(RoomHud.ProfilePath),
            "and they carry no numbers: the numbers option is off until the player turns it on");

        host.PlayerMode("challenge");
        await Frames(4);
        Check(!_hud.Hearts.Grey && !_hud.Bar.Grey && _hud.Hearts.Quarters == 20 && _hud.Hearts.MaxQuarters == 20 && _hud.Bar.RowCount == 1 &&
              Math.Abs(_hud.Bar.MaxM - 0.4f) < 1e-5f && Math.Abs(_hud.Bar.StaminaM - host.Vitals.Stamina) < 0.01f,
            "in Challenge five full red hearts and the starting bar of 0.4 m: one row, as much as the body has");
        // Quarters come from health: a hurt of 30 points (six quarters of 5) leaves fourteen.
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        await Frames(2);
        Check(host.Vitals.Health == 70f && _hud.Hearts.Quarters == 14 && _hud.Hearts.Quarters == host.BodyRules.Quarters(host.Vitals) && !_hud.Hearts.Shaking,
            $"a fall of 30 health points takes six quarters: fourteen left ({_hud.Hearts.Quarters}), the hearts not shaking");
        host.ApplyPlayerDamage(5f, PlayerDamageSource.Fall);
        await Frames(2);
        Check(_hud.Hearts.Quarters == 13 && MeterMath.FillOf(_hud.Hearts.Quarters, 3) == 1, "a hurt of five points is one quarter, and the fourth heart is a quarter full");
        host.ApplyPlayerDamage(40f, PlayerDamageSource.Fall);
        await Frames(2);
        Check(_hud.Hearts.Quarters == 5 && !_hud.Hearts.Shaking && !_hud.Hearts.IsProcessing(), "five quarters is more than one heart: the hearts hold still");
        host.ApplyPlayerDamage(5f, PlayerDamageSource.Fall);
        await Frames(2);
        Check(_hud.Hearts.Quarters == 4 && _hud.Hearts.Shaking && _hud.Hearts.IsProcessing(), "at one heart (four quarters) the hearts shake");
        host.ApplyPlayerDamage(5f, PlayerDamageSource.Fall);
        await Frames(2);
        Check(_hud.Hearts.Quarters == 3 && _hud.Hearts.Shaking, "and keep shaking as it goes down");
        host.PlayerMode("creative");
        await Frames(4);
        Check(_hud.Hearts.Grey && _hud.Hearts.Quarters == 20 && !_hud.Hearts.Shaking && !_hud.Hearts.IsProcessing(),
            "back in Creative the hearts are full and grey again, and still: the hurt belongs to the Challenge body");

        // Many hearts and many bands: two rows of fifteen, a second bar past 2.0 m (a body set by its saved Fractals).
        host.ConfigurePlayerVitals(BodyWith(heartsBought: 25, bandsBought: 9, healthShare: 0.5f));
        host.PlayerMode("challenge");
        await Frames(4);
        Check(_hud.Hearts.HeartCount == 30 && _hud.Hearts.MaxQuarters == 120 && _hud.Hearts.Quarters == 60 && _hud.Hearts.GetCombinedMinimumSize().Y > 2 * HeartsMeter.HeartPx,
            $"thirty hearts, half of them full, in two rows of fifteen ({_hud.Hearts.Quarters} of {_hud.Hearts.MaxQuarters} quarters)");
        Check(_hud.Bar.RowCount == 2 && Math.Abs(_hud.Bar.MaxM - 4.0f) < 1e-4f && Math.Abs(_hud.Bar.RowM - 2.0f) < 1e-4f && Math.Abs(_hud.Bar.BandM - 0.4f) < 1e-4f,
            $"ten bands of 0.4 m are a bar of 4.0 m: two rows of five bands ({_hud.Bar.MaxM:0.0} m in rows of {_hud.Bar.RowM:0.0})");
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(4);
    }

    // ---- the numbers option ----

    private async Task TestMeterNumbers()
    {
        var host = CommandHost.Of(_world)!;
        FreshBodies(host);
        host.PlayerMode("challenge");
        host.ApplyPlayerDamage(30f, PlayerDamageSource.Fall);
        await Frames(4);
        Check(_hud.NumbersToggle is { Text: "Show numbers on the meters", ButtonPressed: false } && !_hud.NumbersToggle.IsPressed(),
            "the customization panel carries the switch \"Show numbers on the meters\", off");
        _hud.SetMeterNumbers(true);
        await Frames(4);
        Check(_hud.MeterNumbers && _hud.HeartsNumbers.Visible && _hud.HeartsNumbers.Text == "Hearts 3½/5" && _hud.BarNumbers.Visible && _hud.BarNumbers.Text == "Stamina bar 0.40/0.40 m",
            $"with it on, the hearts and the bar carry their numbers in the pack's words ({_hud.HeartsNumbers.Text}, {_hud.BarNumbers.Text})");
        Check(RoomHud.LoadMeterNumbers(RoomHud.ProfilePath) && _hud.PipsRow.Numbers == $"Pips {host.Vitals.Pips}/{host.BodyRules.MaxPips}" && _hud.NumbersToggle!.ButtonPressed,
            $"it is kept in the machine's profile with the other switches, and the pool says its numbers too ({_hud.PipsRow.Numbers})");
        _hud.SetMeterNumbers(false);
        await Frames(4);
        Check(!_hud.MeterNumbers && !_hud.HeartsNumbers.Visible && !_hud.BarNumbers.Visible && _hud.PipsRow.Numbers == "" && !RoomHud.LoadMeterNumbers(RoomHud.ProfilePath),
            "and off again: the meters are drawn without numbers, and the profile says so");
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(4);
    }

    // ---- the pool, its prices and the words of an empty one ----

    private async Task TestPoolAndPrices()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        FreshBodies(host);
        host.PlayerMode("creative");
        host.PlayerGoal("stay");
        await Frames(4);

        // Creative: the shadow pool. Eight pips, drawn as a shadow; the price shown; a cast it cannot pay still happens.
        var slotPrice = (Label)_hud.FindChild("SlotPrice", true, false)!;
        Check(_hud.PipsRow.Shadow && _hud.PipsRow.MaxPips == 8 && _hud.PipsRow.Pips == 8 && host.BodyRules.ShadowPool,
            "in Creative the pool is drawn as a shadow pool: eight pips, none spent");
        Check(slotPrice.Visible && slotPrice.Text == Dots(2) && _hud.SlotPrice(0) == 2 && _hud.SlotPrice(1) == 4 && _hud.SlotPrice(2) == 2 && _hud.SlotPrice(3) == 2 && _hud.SlotPrice(4) == 0,
            $"the key by Glow's button says what it costs, two dots (Grow four, Bubbles and Fireworks two, Build nothing): \"{slotPrice.Text}\"");
        Check(HelpLines().Any(line => line.StartsWith($"1 Glow {Dots(2)}, 2 Grow {Dots(4)}, 3 Bubbles {Dots(2)}, 4 Fireworks {Dots(2)}", StringComparison.Ordinal)),
            "and so does the help's line for the ability keys: " + HelpLines()[8]);
        var spot = FloorNearGubble(0.3f);
        for (var i = 0; i < 4; i++)
        {
            _hud.AimRayForTests = Down(spot + new Vector3(0.02f * i, 0, 0));
            Check(CastGlow(host, spot + new Vector3(0.02f * i, 0, 0)), $"Glow {i + 1} in Creative is cast");
        }
        await Frames(4);
        Check(host.Vitals.Pips == 0 && _hud.PipsRow.Pips == 0 && slotPrice.Text == "needs 2" && Math.Abs(slotPrice.Modulate.A - 0.5f) < 0.01f &&
              HelpLines().Any(line => line.Contains("1 Glow needs 2, 2 Grow needs 4", StringComparison.Ordinal)),
            $"four Glows drain the shadow pool: the key now says \"{slotPrice.Text}\", faint, and the help line says it too");
        var fifth = CastGlow(host, spot + new Vector3(0.1f, 0, 0));
        await Frames(2);
        Check(fifth && host.Vitals.Pips == 0 && _hud.PipsRow.Pips == 0,
            "a cast the shadow pool cannot pay still happens, and the pool stays at none");
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var prices = _hud.Wheel.PriceLabels;
        Check(prices[0].Visible && prices[0].Text == "needs 2" && Math.Abs(prices[0].Modulate.A - 0.5f) < 0.01f && prices[4].Text == "needs 4" && Math.Abs(prices[4].Modulate.A - 0.5f) < 0.01f &&
              !prices[3].Visible && !prices[6].Visible && !prices[7].Visible && !prices[5].Visible,
            "on the wheel, Glow says \"needs 2\" and Grow \"needs 4\", faint in Creative; the orders and Build (the island has none) say nothing");
        _hud._UnhandledInput(Press(Key.Escape));
        _hud._UnhandledInput(RightButton(false));
        _hud.StopGubble();
        await Frames(4);

        // Challenge: a real pool. Price by principal: the player's order is the full price, the Gubble's own idea half.
        FreshBodies(host);
        host.PlayerMode("challenge");
        await Frames(4);
        Check(!_hud.PipsRow.Shadow && _hud.PipsRow.Pips == 8 && _hud.PipsRow.MaxPips == 8 && !host.BodyRules.ShadowPool && slotPrice.Text == Dots(2) && Math.Abs(slotPrice.Modulate.A - 1f) < 0.01f,
            "in Challenge the pool is real: eight pips drawn as dots, the key says two dots");
        Check(_hud.SlotPrice(0) == host.PriceFor("glow", CommandHost.PlayerPrincipal) && _hud.SlotPrice(0) == 2 && host.PriceFor("glow", CommandHost.CompanionPrincipal) == 1 &&
              _hud.SlotPrice(1) == 4 && host.PriceFor("grow", CommandHost.CompanionPrincipal) == 2,
            "the HUD shows what the player pays, the ordered price (Glow 2, Grow 4), never the Gubble's own-idea half");
        for (var i = 0; i < 3; i++) CastGlow(host, spot + new Vector3(0.02f * i, 0, 0));
        await Frames(4);
        Check(host.Vitals.Pips == 2 && _hud.PipsRow.Pips == 2 && _hud.PipsRow.MaxPips == 8,
            "three Glows spend six pips: two of eight are left on the dots");
        var needs4 = HelpLines().Any(line => line.Contains($"1 Glow {Dots(2)}, 2 Grow needs 4, 3 Bubbles {Dots(2)}, 4 Fireworks {Dots(2)}", StringComparison.Ordinal));
        Check(needs4 && slotPrice.Text == Dots(2), "with two pips left Grow's hint says \"needs 4\" and the others still read as their price: " + HelpLines()[8]);
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        Check(prices[0].Text == Dots(2) && prices[1].Text == Dots(2) && prices[8].Text == Dots(2) && prices[4].Text == "needs 4" && Math.Abs(prices[4].Modulate.A - 1f) < 0.01f && !prices[5].Visible,
            "on the wheel Grow says \"needs 4\" at full strength (the pool is real) and the cheaper abilities show their dots");
        _hud._UnhandledInput(Press(Key.Escape));
        _hud._UnhandledInput(RightButton(false));
        CastGlow(host, spot + new Vector3(0.1f, 0, 0));
        await Frames(4);
        Check(host.Vitals.Pips == 0 && _hud.PipsRow.Pips == 0 && slotPrice.Text == "needs 2",
            "the fourth Glow empties the pool: all eight pips are spent rings and the key says \"needs 2\"");
        // An empty pool: the host refuses with its own words, and the HUD says them; the pips blink.
        var effects = host.ActiveEffectIds.Count;
        _hud._UnhandledInput(Press(Key.Key1));
        await Frames(3);
        var notice = (Label)_hud.FindChild("Notice", true, false)!;
        Check(host.ActiveEffectIds.Count == effects && _hud.Cue.State == "refused" && _hud.Cue.Reason == "The Gubble needs a moment" && notice.Text == CommandHost.EnergyExhaustedMessage &&
              _hud.PipsRow.FlashLeftS > 0,
            $"1 with no pips: nothing is cast, the Gubble shakes its head with \"The Gubble needs a moment\" (the host's words), the pips blink: \"{_hud.Cue.Reason}\" / \"{notice.Text}\"");
        _hud.StopGubble();

        // The journal: the Abilities page says what each costs, the sheet the pool.
        _hud.OpenJournal();
        await Frames(JournalMoveFrames);
        _hud.Journal.OpenPage(JournalPages.Abilities);
        var abilities = _hud.Journal.PageText;
        var fireAt = abilities.IndexOf("· Fire", StringComparison.Ordinal);
        Check(abilities.Contains("Costs 2 pips", StringComparison.Ordinal) && abilities.Contains("Costs 4 pips", StringComparison.Ordinal) && !abilities.Contains("Costs 6 pips", StringComparison.Ordinal) &&
              fireAt >= 0 && abilities.IndexOf("Costs 2 pips", fireAt, StringComparison.Ordinal) > fireAt,
            "the journal's Abilities page names each ability's cost in pips (Glow 2, Grow 4, Fire 2: decision 83)");
        _hud.Journal.OpenPage(JournalPages.Character);
        var sheet = _hud.Journal.PageText;
        Check(sheet.Contains("Pips 0/8", StringComparison.Ordinal) && sheet.Contains("Logs and stones: ", StringComparison.Ordinal) && sheet.Contains("Health ", StringComparison.Ordinal),
            "and the character page keeps its numbers: health, stamina, the pool (Pips 0/8), and the logs and stones (the wallet is retired)");
        _hud.CloseJournal();
        await Frames(JournalMoveFrames);

        FreshBodies(host);
        host.PlayerMode("creative");
        host.PlayerGoal("stop");
        host.PlayerGoal("stay");
        _hud.AimRayForTests = Air();
        await Frames(4);
    }

    /// <summary>The reticle's tag names what a tap that lights a spot costs.</summary>
    private async Task TestAskTagPrice()
    {
        var host = CommandHost.Of(_world)!;
        FreshBodies(host);
        host.PlayerMode("challenge");
        host.PlayerGoal("stay");
        var spot = FloorNearGubble(0.3f);
        _hud.LightForTests = _ => 0.05f;
        _hud.AimRayForTests = Down(spot);
        await Frames(12);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.Glow && _hud.AskTag.Visible && _hud.AskTag.Text.EndsWith(" " + Dots(2), StringComparison.Ordinal),
            $"aimed at a dark spot in Glow's reach, the reticle says what the tap costs: \"{_hud.AskTag.Text}\"");
        for (var i = 0; i < 4; i++) CastGlow(host, spot);
        await Frames(12);
        Check(_hud.AskTag.Text.EndsWith(" needs 2", StringComparison.Ordinal), $"and \"needs 2\" once the pool cannot pay it: \"{_hud.AskTag.Text}\"");
        _hud.StopGubble();
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(4);
    }

    // ---- the minimap ----

    private async Task TestMinimap()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        var map = _hud.Map;
        var home = player.GlobalPosition;
        _hud.SetViewMode(1);
        await Frames(30);
        var screen = _hud.GetViewport().GetVisibleRect();
        var rect = map.GetGlobalRect();
        Check(map.IsVisibleInTree() && rect.End.X <= screen.End.X + 0.5f && rect.Position.X >= screen.End.X - SandboxControls.FocusWords.MinimapWidthPx &&
              rect.Position.Y >= 0 && rect.End.Y <= SandboxControls.FocusWords.MinimapHeightPx,
            $"the minimap sits in the top-right corner the focus tag keeps clear, from the first minute ({rect} in {screen})");
        // Where the avatars have been, the host's sight has filled the map in; the minimap reads it when they move, as the journal does.
        player.TryTeleportTo(player.GlobalPosition + new Vector3(0.2f, 0, 0));
        player.ResetPhysicsInterpolation();
        await Frames(80);
        var behind = new Node[] { _hud.Wheel, _hud.Book, _hud.FindChild("MagicPanel", true, false)!, _hud.GetNode<PanelContainer>("HelpFooter") };
        Check(behind.All(n => n.GetIndex() > map.GetIndex()),
            "the minimap draws under the wheel, the Magic panel, the help and the book (the wheel's title reaches into its corner)");
        Check(map.Builds >= 1 && map.InkPixels > 0 && map.Strips > 0, $"it is drawn from the team's discovered map: {map.Strips} strips of ground seen, {map.InkPixels} pixels of ink");
        // Everything inked was seen: each inked pixel's ground is in the host's discovered map (read here on its own, the journal's data).
        var data = new DiscoveredMapData();
        data.Read(host.ExportDiscovered());
        var unseen = 0;
        var inked = 0;
        for (var y = 0; y < Minimap.ViewPx; y++)
            for (var x = 0; x < Minimap.ViewPx; x++)
            {
                if (!map.InkAt(x, y)) continue;
                inked++;
                var at = map.MapOf(x, y);
                var cell = new Rect2(at - Vector2.One * map.MetresPerPixel * 0.5f, Vector2.One * map.MetresPerPixel).Grow(0.0001f);
                if (!data.Land.Any(land => land.Area.Intersects(cell, true))) unseen++;
            }
        Check(inked == map.InkPixels && unseen == 0, $"every inked pixel is ground the team has seen ({inked} pixels, {unseen} not in the discovered map)");
        // The cadence: the map is read about once a second while the avatars stand, never every frame.
        var builds = map.Builds;
        await Frames(120);
        Check(map.Builds - builds <= 2, $"read at most once a second, and not while nothing moves ({map.Builds - builds} reads in two seconds)");
        // A body that moves makes the map read again, and its mark follows it.
        var from = map.PixelOf(new Vector2(player.GlobalPosition.X, player.GlobalPosition.Z));
        player.TryTeleportTo(player.GlobalPosition + new Vector3(0.5f, 0, 0));
        player.ResetPhysicsInterpolation();
        await Frames(80);
        var to = map.PixelOf(new Vector2(player.GlobalPosition.X, player.GlobalPosition.Z));
        Check(map.Builds > builds && to.X >= 0 && to.X < Minimap.ViewPx && to.Y >= 0 && to.Y < Minimap.ViewPx,
            $"a body that moves makes the map read again, and the player's mark stays on it ({from.X:0.0},{from.Y:0.0} to {to.X:0.0},{to.Y:0.0} px)");
        Check(map.LastBuildMs < 5.0, $"reading and painting the map takes {map.LastBuildMs:0.00} ms");
        player.TryTeleportTo(home);
        player.ResetPhysicsInterpolation();
        await Frames(10);
    }

    // ---- what the meters cost a frame ----

    /// <summary>
    /// The budget (docs/runs/RUN-2-MECHANICS.md section 9): the meters and the minimap add at most 0.5 ms a frame. Measured in a busy
    /// state (Challenge, the Gubble following, the player stepping about so the map is read again about once a second, the hearts
    /// at one heart so they shake), after a warm-up, over a few hundred frames; and in a steady one, where nothing changes, they
    /// allocate nothing.
    /// </summary>
    private async Task TestMeterFrameCost()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        FreshBodies(host);
        host.PlayerMode("challenge");
        host.ApplyPlayerDamage(80f, PlayerDamageSource.Fall);
        host.PlayerGoal("stay");
        _hud.SetViewMode(1);
        await Frames(120);
        MeterCost.Reset();
        MeterCost.Measuring = true;
        var start = player.GlobalPosition;
        for (var i = 0; i < 600; i++)
        {
            if (i % 60 == 0)
            {
                player.TryTeleportTo(start + new Vector3(0.06f * (i / 60 % 4), 0, 0));
                player.ResetPhysicsInterpolation();
            }
            await Frames(1);
        }
        MeterCost.Measuring = false;
        player.TryTeleportTo(start);
        player.ResetPhysicsInterpolation();
        var (mean, worst, frames, reads) = (MeterCost.MeanMs, MeterCost.WorstMs, MeterCost.Frames, _hud.Map.Builds);
        GD.Print($"PLAY_HUD_COST: meters, pips and minimap per frame in a busy HUD: mean {mean:0.0000} ms, worst {worst:0.000} ms over {frames} frames ({reads} map reads so far, the last {_hud.Map.LastBuildMs:0.00} ms), {MeterCost.AllocatedBytes / Math.Max(1, frames)} bytes allocated a frame");
        GD.Print("PLAY_HUD_COST: by piece, ms a frame: " + string.Join(", ", Enum.GetValues<MeterCost.Piece>().Select(p => $"{p} {MeterCost.PieceMeanMs(p):0.0000}")));
        var slow = MeterCost.SamplesMs.Select((ms, i) => (ms, i)).OrderByDescending(s => s.ms).Take(6).Select(s => $"{s.ms:0.00}@{s.i}").ToArray();
        GD.Print($"PLAY_HUD_COST: the slowest frames (ms@frame): {string.Join(", ", slow)}; the median {MeterCost.SamplesMs.OrderBy(m => m).ElementAt(MeterCost.SamplesMs.Count / 2):0.000} ms");
        // The budget is held by the mean and by all but the rarest frames (the 99th percentile): a frame that a collection or the
        // machine's other work happened to land on is not the meters' cost, and the worst frame is printed above for the record.
        var p99 = MeterCost.SamplesMs.OrderBy(m => m).ElementAt((int)(MeterCost.SamplesMs.Count * 0.99));
        Check(frames >= 550 && mean < 0.5 && p99 < 0.5,
            $"the meters, the pips and the minimap cost {mean:0.000} ms a frame, 99 frames in a hundred under {p99:0.000} ms (the worst {worst:0.000} ms), against a budget of 0.5 ms");
        // Steady: the hearts hold still, the Gubble waits, nobody moves: nothing is allocated by what they do each frame.
        host.ConfigurePlayerVitals(new MeterStore());
        host.PlayerGoal("stay");
        await Frames(200);
        _hud.Map.Read(host);
        await Frames(2);
        var readsBefore = _hud.Map.Builds;
        MeterCost.Reset();
        MeterCost.Measuring = true;
        await Frames(240);
        MeterCost.Measuring = false;
        GD.Print("PLAY_HUD_COST: steady by piece, ms a frame: " + string.Join(", ", Enum.GetValues<MeterCost.Piece>().Select(p => $"{p} {MeterCost.PieceMeanMs(p):0.0000}")));
        GD.Print($"PLAY_HUD_COST: steady, {MeterCost.AllocatedBytes} bytes allocated by the meters and minimap over {MeterCost.Frames} frames, mean {MeterCost.MeanMs:0.0000} ms");
        Check(MeterCost.Frames >= 200 && _hud.Map.Builds == readsBefore && MeterCost.AllocatedBytes / Math.Max(1, MeterCost.Frames) <= 64,
            $"and when nothing changes (the map not read, nobody moving) they allocate next to nothing ({MeterCost.AllocatedBytes} bytes over {MeterCost.Frames} frames)");
        FreshBodies(host);
        host.PlayerMode("creative");
        host.PlayerGoal("stop");
        host.PlayerGoal("stay");
        await Frames(4);
    }

    // ---- the journal reads the pack's word ----

    private async Task TestExpertiseWords()
    {
        var host = CommandHost.Of(_world)!;
        _hud.OpenJournal();
        await Frames(JournalMoveFrames);
        var panel = _hud.Journal;
        panel.OpenPage(JournalPanel.IndexPage);
        var links = panel.Lines.Where(line => line.Ink == JournalInk.Link).ToArray();
        Check(links.Select(l => l.Target).SequenceEqual(JournalPages.Sections) && links[0].Text == "01   Expertise" && links[1].Text == "02   Abilities",
            "the index's first page is called Expertise (the pack's word, MECHANICS decision J), still turning to the Discoveries page: " + links[0].Text);
        panel.OpenPage(JournalPages.Discoveries);
        var heading = (Label)panel.FindChild("JournalHeading", true, false)!;
        Check(panel.Page == JournalPages.Discoveries && heading.Text == "Expertise" && JournalPages.Title(JournalPages.Discoveries, host) == "Expertise" && JournalPages.Title(JournalPages.Map, host) == "Map",
            "that page's heading reads Expertise, and no other page's name changes: " + heading.Text);
        Check(!panel.PageText.Contains("Your Glow discoveries", StringComparison.Ordinal),
            "and its words say expertise where they said discoveries");
        Check(MeterWords.Name(host, "hearts") == "Hearts" && MeterWords.Name(host, "stamina") == "Stamina bar" && MeterWords.Name(host, "pips") == "Pips" &&
              MeterWords.Name(host, "fractals") == "Fractals" && MeterWords.Name(host, "camp") == "Camp" && MeterWords.Name(host, "trust") == "Trust" &&
              MeterWords.Name(host, "expertise") == "Expertise",
            "the names block's words reach the HUD: hearts, stamina bar, pips, Fractals, expertise, camp and Trust");
        _hud.CloseJournal();
        await Frames(JournalMoveFrames);
    }

    // ---- the gather key ----

    /// <summary>A copy of the test room with a loose log beside the spawn (a bark thing the player can pick up and gather), under the suite's saves.</summary>
    private static string GatherRoom()
    {
        var source = ProjectSettings.GlobalizePath(RoomWorld.DefaultRoom);
        var shown = HudSaves + "/gather_room/test_room";
        var target = ProjectSettings.GlobalizePath(shown);
        if (Directory.Exists(target)) Directory.Delete(ProjectSettings.GlobalizePath(HudSaves + "/gather_room"), true);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal)))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "room.json")))!.AsObject();
        var files = manifest["files"]!.AsArray();
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "objects/doorstop_proxy/asset.json")))!.AsObject();
        record["asset_id"] = "log_proxy";
        record["display_name"] = "Log";
        record["category"] = "woodpile";
        record["category_group"] = "decor";
        record["dimensions_m"] = new JsonArray(0.03, 0.03, 0.1);
        record["physics"] = new JsonObject { ["movable"] = true, ["mass_kg"] = 0.03 };
        record["materials"] = new JsonArray(new JsonObject { ["slot"] = "body", ["role"] = "bark", ["base_color"] = "#8b6f59" });
        record["affordances"] = new JsonArray("walkable_top");
        var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
        Directory.CreateDirectory(Path.Combine(target, "objects", "log_proxy"));
        File.WriteAllBytes(Path.Combine(target, "objects/log_proxy/asset.json"), bytes);
        files.Add(new JsonObject { ["path"] = "objects/log_proxy/asset.json", ["sha256"] = CanonicalJson.Sha256Hex(bytes), ["bytes"] = bytes.Length });
        manifest["objects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "obj:log", ["asset"] = "objects/log_proxy/asset.json",
            ["transform"] = new JsonObject { ["position_m"] = new JsonArray(0.12, 0.0, 0.62), ["rotation"] = new JsonArray(0.0, 0.0, 0.0, 1.0) },
            ["support"] = new JsonObject { ["kind"] = "shell", ["target_id"] = "shell:floor" },
        });
        File.WriteAllText(Path.Combine(target, "room.json"), manifest.ToJsonString(), new UTF8Encoding(false));
        return shown;
    }

    /// <summary>
    /// E gathers what the hands hold into the pockets through item.gather, as the player's command (P-Inventory-2: the wallet is retired, a
    /// log is a unit of its stack): a held log leaves the island as one durable revision and the notice names the pockets' logs and stones
    /// after it; the journal's sheet and the Magic panel name them too.
    /// </summary>
    private async Task TestGatherKey()
    {
        var previousSaves = CommandHost.SaveRoot;
        CommandHost.SaveRoot = HudSaves + "/gather";
        try
        {
            var room = GatherRoom();
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            world.RoomDirectory = room;
            AddChild(world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "(the room with a log boots: " + world.LoadError + ")");
            if (!world.WorldReady) return;
            await Frames(5);
            var host = CommandHost.Of(world)!;
            var hud = world.GetNode<RoomHud>("RoomHud");
            var notice = (Label)hud.FindChild("Notice", true, false)!;
            world.Player.ReadKeyboard = false;
            Check(PlayerControls.Label(Act.Interact) == "E" && Act.Interact == "item_gather" && PlayerControls.Specs.Any(spec => spec.Action == Act.Interact) && InputMap.HasAction(Act.Interact),
                "the interaction key is E, the input map's item_gather action (its name is from when E only gathered) and in the list of controls");
            Check(hud.FindChild("HandActions", true, false)!.GetChildren().OfType<Button>().Any(b => b.Text == "E Use"), "the hands' row has its button, naming the key");
            // Away from the campfire and facing nothing, with empty hands: E has nothing to do and says what it works on.
            var spawnAt = world.Player.GlobalPosition;
            Check(world.Player.TryTeleportTo(spawnAt + new Vector3(-0.45f, 0, 0)), "(the player steps away from the campfire and the log)");
            world.Player.Rotation = new Vector3(0, Mathf.Pi / 2, 0);
            await Frames(40);
            var revision = host.Revision;
            var idle = hud.Interaction;
            hud._UnhandledInput(Press(Key.E));
            await Frames(2);
            Check(idle.Kind == RoomHud.InteractKind.None && host.Revision == revision && notice.Text == hud.InteractNothingWords() && host.HeldBy(CommandHost.PlayerAvatar) == null,
                "E with empty hands and nothing in front changes nothing and says what it works on: " + notice.Text);
            Check(world.Player.TryTeleportTo(spawnAt), "(back by the log)");
            await Frames(10);
            // The log in hand (picked up as the player, a command); a thing that is no log is refused.
            int Logs() => host.PocketEntries.FirstOrDefault(e => e.Item == "log")?.Count ?? 0;
            var wallet = Logs();
            var grab = host.HandleObject(CanonicalJson.Text(new JsonObject
            {
                ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "hud-grab-log", ["room_id"] = host.Room.RoomId, ["op"] = "entity.grab",
                ["args"] = new JsonObject { ["target"] = "obj:log", ["actor"] = CommandHost.PlayerAvatar },
            }), CommandHost.PlayerPrincipal);
            Check(grab["ok"]?.GetValue<bool>() == true && host.HeldBy(CommandHost.PlayerAvatar) == "obj:log", "the player picks the log up");
            await Frames(40);
            // By the campfire the chest comes first (decision 113); the wallet is where a log is banked away from a camp.
            Check(hud.Interaction.Kind == RoomHud.InteractKind.Store && hud.Interaction.Words == "E: store Log in the chest" && hud.InteractPrompt.Visible,
                "holding the log at the camp, E's prompt says it stores it in the chest first (the hands first): " + hud.Interaction.Words);
            Check(world.Player.TryTeleportTo(spawnAt + new Vector3(-0.9f, 0, 0)), "(the player carries the log a few steps from the camp)");
            await Frames(40);
            Check(hud.Interaction.Kind == RoomHud.InteractKind.Gather && hud.Interaction.Words.StartsWith("E: gather ", StringComparison.Ordinal) && hud.Interaction.Words.EndsWith(" into your wallet", StringComparison.Ordinal) &&
                  hud.InteractPrompt.Visible && hud.InteractPrompt.Text == hud.Interaction.Words,
                "away from a camp, E's prompt says it gathers it into the wallet (the hands first; a log is the wallet's, not the pockets'): " + hud.Interaction.Words);
            revision = host.Revision;
            hud._UnhandledInput(Press(Key.E));
            await Frames(2);
            var wood = Logs();
            Check(wood == wallet + CommandHost.GatherUnits && host.Revision == revision + 1 && host.HeldBy(CommandHost.PlayerAvatar) == null && host.Nature.Objects["obj:log"].Gathered,
                $"E gathers it through the host: one durable revision, the log gone, the pockets' logs {wallet} to {wood}");
            Check(notice.Text == $"Gathered. In your pockets: {wood} log", "and the notice says what the pockets hold of logs and stones now: " + notice.Text);
            hud._UnhandledInput(Press(Key.E));
            await Frames(2);
            Check(Logs() == wood && host.Revision == revision + 1 && host.HeldBy(CommandHost.PlayerAvatar) == null,
                "a second E, hands empty, gathers nothing more: " + notice.Text);
            if (hud.Sitting) hud.RestKey();
            // The sheet names the logs and stones (the wallet is retired).
            hud.OpenJournal();
            await Frames(JournalMoveFrames);
            hud.Journal.OpenPage(JournalPages.Character);
            Check(hud.Journal.PageText.Contains($"Logs and stones: {wood} log in your pockets", StringComparison.Ordinal) && !hud.Journal.PageText.Contains("wallet", StringComparison.Ordinal),
                "and the journal's character page lists the pockets' log, and no wallet");
            hud.CloseJournal();
            await Frames(JournalMoveFrames);
            // The Island page shows the logs and stones in every mode.
            hud.ToggleMagicPanel();
            await Frames(14);
            var materials = (Label)hud.FindChild("Island", true, false)!.FindChildren("*", "Label", true, false).OfType<Label>().First(l => l.Text.Contains("Logs and stones", StringComparison.Ordinal));
            Check(materials.Text.Contains($"{wood} log", StringComparison.Ordinal), "the Magic panel's Island page names the logs and stones in every mode: " + materials.Text.Replace("\n", " | "));
            hud.ToggleMagicPanel();
            world.Free();
            await Frames(2);
        }
        finally
        {
            CommandHost.SaveRoot = previousSaves;
        }
    }
}
