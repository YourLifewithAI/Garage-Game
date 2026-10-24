using Godot;
using System;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Connection energy (docs/game/MECHANICS.md section 5; contracts/README.md "Price by principal"): whole pips round the
/// Gubble that pay for its abilities outside Creative, one pool per team per mode kept with the player's vitals.
/// - The price follows the principal alone, never a field: the player's keys, wheel and HUD asks pay the ordered price;
///   every command on the Gubble's connection pays the own-idea price, except while a wish runs (then every Gubble command
///   pays as ordered, whatever its action id says, since a companion-written tag could be left off to pay less).
/// - The guard runs once, at commit, after every other check: a preview, a stop, a replay and a refused command cost
///   nothing; a held command pays its own principal's price when the player's click commits it. An empty pool refuses with
///   energy_exhausted, carrying the balance and the price; in Creative the shadow pool drains and never refuses.
/// - The refill: one pip every refill_near_every_s while the Gubble is within refill_near_within_m of the player, one every
///   refill_far_every_s apart, the difficulty's refill multiplier applied; at camp and after time away the pool wakes at
///   least camp_fraction full. The maximum rises one pip per expertise_per_pip expertise, never past the ceiling.
/// </summary>
public partial class CommandHost
{
    /// <summary>The words of an empty pool (on screen: the Gubble needs a moment), the mock host's too.</summary>
    public const string EnergyExhaustedMessage = "The Gubble needs a moment.";
    /// <summary>Progress toward the next refilled pip (a pip at 1).</summary>
    private double _pipProgress;

    /// <summary>
    /// What casting an ability costs this principal now, in whole pips (0 without a pool or a price): the HUD's "needs 4". The
    /// Gubble's own-idea price carries Trust's band discount (M8, CommandHostTrust.TrustPrice).
    /// </summary>
    public int PriceFor(string capability, string principal) =>
        TrustPrice(capability, principal, _vitalsRules.Price(capability, principal != PlayerPrincipal, principal != PlayerPrincipal && WishRunning()));

    /// <summary>
    /// The guard at commit: the price this principal pays for this ability, refused with energy_exhausted when a real pool
    /// cannot pay it. The shadow pool never refuses. No allocation on the way through (a dictionary lookup and a compare);
    /// the caller charges the price once the command has committed (ChargeEnergy).
    /// </summary>
    internal int GuardEnergy(string capability, string principal)
    {
        var price = PriceFor(capability, principal);
        if (price <= 0 || _vitalsRules.ShadowPool) return price;
        if (_playerVitalsUnavailable) throw new Refusal("not_ready", VitalsNotice, retryable: true);
        var balance = Vitals.Pips < 0 ? 0 : Vitals.Pips;
        if (balance < price)
            throw new Refusal("energy_exhausted", EnergyExhaustedMessage, "$.args.capability", retryable: true) { Balance = balance, Price = price };
        return price;
    }

    /// <summary>The charge, once the command committed: the pool (or the shadow pool) loses the price, never below empty.</summary>
    private void ChargeEnergy(int price)
    {
        NoteTrustCharge(price); // decision 69: Trust reads what the guard charged, never an op list (CommandHostTrust)
        if (price <= 0 || _playerVitalsUnavailable || !_vitalsRules.HasPool) return;
        var pips = Math.Max(0, Math.Max(0, Vitals.Pips) - price);
        if (pips == Vitals.Pips) return;
        _playerModes = _playerModes.WithCurrent(Vitals.With(pips: pips));
        _vitalsDirty = true;
    }

    /// <summary>Whether a wish is running now (its Gubble commands pay as ordered). A loop, so the guard allocates nothing.</summary>
    private bool WishRunning()
    {
        var items = Wishes.Items;
        for (var i = 0; i < items.Count; i++)
            if (items[i].Status == "running") return true;
        return false;
    }

    /// <summary>The pool's refill for one vitals tick: faster while the Gubble is near the player, the difficulty's pace applied.</summary>
    private void RefillPips(float seconds)
    {
        if (!_vitalsRules.HasPool || _playerVitalsUnavailable || seconds <= 0) return;
        var vitals = Vitals;
        var max = _vitalsRules.MaxPips;
        if (vitals.Pips >= max)
        {
            _pipProgress = 0;
            return;
        }
        var distance = Player != null && IsInstanceValid(Player) && Companion != null && IsInstanceValid(Companion)
            ? Player.GlobalPosition.DistanceTo(Companion.GlobalPosition) : float.PositiveInfinity;
        _pipProgress += seconds / _vitalsRules.RefillEveryS(distance);
        if (_pipProgress < 1) return;
        var gained = (int)Math.Floor(_pipProgress);
        _pipProgress -= gained;
        var pips = Math.Min(max, Math.Max(0, vitals.Pips) + gained);
        if (pips >= max) _pipProgress = 0;
        _playerModes = _playerModes.WithCurrent(vitals.With(pips: pips));
        _vitalsDirty = true;
    }

    // ---- pace lines (MECHANICS section 6: one per reward in the journal export) ----

    /// <summary>
    /// A reward's pace line: a 'found' fact in the journal (its real time is the pace), in the game's own words and counted,
    /// never an award, an announcement or a hint. Fractals found, expertise learned, the pool's maximum rising, and a heart or a
    /// band grown from Fractals at the camp.
    /// </summary>
    private void NotePace(string reward, int count)
    {
        var line = reward switch
        {
            "fractal" => $"You found a {NamesRules.Singular(NameOf("fractals"))} ({count} found).",
            "expertise" => $"The Gubble learned something new ({count} {NameOf("expertise").ToLowerInvariant()}).",
            "pips" => $"The Gubble's pool grew to {count} {NameOf("pips").ToLowerInvariant()}.",
            "heart" => $"You grew a new heart ({count} {NameOf("hearts").ToLowerInvariant()}).",
            "band" => $"Your {NameOf("stamina").ToLowerInvariant()} grew a band ({count} bands).",
            _ => "",
        };
        if (line.Length == 0 || Authority == null) return;
        var fact = Fact("found", line, PlayerPrincipal, PlayerPrincipal, null, null, null);
        fact["quantity"] = new JsonObject { ["count"] = Math.Clamp(count, 0, 1_000_000) };
        AddHistory(fact);
        SaveTeam();
    }
}
