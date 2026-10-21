using System;
using System.Collections.Generic;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The rules the player's body and the pool play by now (VitalsRules): the island's pack, the mode, the difficulty, the
/// team's knacks and the expertise learned. This file is the modes packet's (M10 of docs/runs/RUN-2-MECHANICS.md): the
/// difficulty and the knacks are set here by the boot (the teams packet, M3, from the island save and the team file),
/// never by a request; the Live rows stay refused at the menu and play as Challenge Standard meanwhile (VitalsRules).
/// </summary>
public partial class CommandHost
{
    /// <summary>The Challenge difficulty this save plays at: gentle, standard (the default, today's) or brave. Not read in Creative or Live.</summary>
    public string Difficulty { get; private set; } = "standard";
    /// <summary>The team's knacks by id with their ranks (rank 1 first), as the team file holds them. Empty: no knacks, today's body.</summary>
    public IReadOnlyDictionary<string, int> Knacks => _knacks;
    /// <summary>The rules the body and the pool play by now.</summary>
    public VitalsRules BodyRules => _vitalsRules;
    private IReadOnlyDictionary<string, int> _knacks = new Dictionary<string, int>(StringComparer.Ordinal);
    private VitalsRules _vitalsRules = VitalsRules.For(null, "creative");

    /// <summary>
    /// The boot's difficulty for this save (the menu's choice, kept in the island save). Anything else keeps the current one, and so does
    /// a difficulty the island's pack does not offer (its modes rows say `available: false`, or leave the difficulty out): the rules
    /// never play a row the pack withheld. A pack without a modes block has no difficulties to withhold. Returns whether it was used.
    /// </summary>
    public bool UseDifficulty(string difficulty)
    {
        if (difficulty is not ("gentle" or "standard" or "brave")) return false;
        if (Rules?.Modes != null && !Rules.ModeAvailable("challenge", difficulty)) return false;
        Difficulty = difficulty;
        ResolveVitalsRules();
        return true;
    }

    /// <summary>
    /// A torch left lying in dry grass catches by itself: the Live row's switch, true only while this body plays Live (which the menu
    /// and the boot refuse until Live is built, so it is false in every game today). The torch round reads it; nothing else does.
    /// </summary>
    public bool TorchSelfIgnite => _vitalsRules.TorchSelfIgnite;

    // ---- the names block, served to the screen ----

    /// <summary>
    /// What the screen calls hearts, stamina, pips, fractals, expertise, camp and trust on this island (the pack's names block): the
    /// pack's word through KernelText.DisplayText at the names' cap, never as the pack wrote it, else today's word when the pack has no
    /// block or leaves the name out. A name is data for the screen and the journal: it never reaches a command, an id or an icon.
    /// </summary>
    public string NameOf(string key) => Rules?.Names?.Word(key) ?? NamesRules.Today(key);

    /// <summary>What the screen calls a knack on this island: the pack's name (sanitised as NameOf's), else the knack's id in words.</summary>
    public string KnackName(string id) => Rules?.Names?.Knack(id) ?? NamesRules.TodayKnack(id);

    /// <summary>The team's knacks (the team file's), by id and rank: a knack the pack lacks or has not made available counts for nothing.</summary>
    public void UseKnacks(IReadOnlyDictionary<string, int> ranks)
    {
        var copy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, rank) in ranks)
            if (!string.IsNullOrEmpty(id) && rank is >= 1 and <= 3) copy[id] = rank;
        _knacks = copy;
        ResolveVitalsRules();
    }

    /// <summary>
    /// The knacks that move the body's own numbers (walk and run, the hop, the swim, the safe drop in metres) are read from the pack's
    /// table for the team's ranks and handed to the body (BodyKnacks, M5). The rest (Big bar, Steady climber, Long breath, Big heart)
    /// are VitalsRules' own. No knack: today's body.
    /// </summary>
    private void ApplyBodyKnacks()
    {
        if (Player != null && IsInstanceValid(Player)) Player.Knacks = BodyKnacks.FromPack(Rules?.Knacks, _knacks);
    }

    /// <summary>The expertise learned by the pair in every ability (Glow's, Bubbles', Fire's and Grow's), which raises the pool's maximum one pip per expertise_per_pip.</summary>
    public int ExpertiseCount() => GlowProgress.ExpertiseCount;

    private VitalsRules ResolvedRules(string mode) => VitalsRules.For(Rules, mode, Difficulty, _knacks, ExpertiseCount());

    /// <summary>
    /// The rules again, after the pack, the mode, the difficulty, the knacks or the expertise changed: the body is brought to
    /// them (its maxima, whole quarters, the pool within its maximum). Until play begins, that is the body as the store gave it
    /// (CommandHostModes' BroughtTo: a launch reads the vitals before the pack, the knacks and the difficulty, and each of them
    /// brings the saved record, never what the rules before made of it; the time away caught up under them); with no rules
    /// loaded the body is kept as it is. In play, the body as it is now. With pace (a new expertise), a raised pool maximum is a
    /// pace line.
    /// </summary>
    private void ResolveVitalsRules(bool pace = false)
    {
        var before = _vitalsRules;
        _vitalsRules = ResolvedRules(Mode);
        ApplyBodyKnacks();
        if (!_vitalsRules.InBands) _slowed = false;
        if (_playerVitalsUnavailable) return;
        var asRead = BodyAsRead;
        if (!asRead) _readBody = null;
        var next = asRead ? BroughtTo(_vitalsRules, _readBody!, _readWritten) : Rules == null ? Vitals : _vitalsRules.Normalize(Vitals);
        if (!ReferenceEquals(next, Vitals) && !next.Same(Vitals))
        {
            _playerModes = _playerModes.WithCurrent(next);
            _vitalsDirty = true;
        }
        if (asRead)
        {
            _readResolved = Vitals;
            // The bar as read: empty, no running until it refills (as ResetVitalsCounters says at the read).
            _slowed = _vitalsRules.InBands && Vitals.Stamina <= 0;
        }
        if (pace && _vitalsRules.HasPool && before.HasPool && before.Mode == _vitalsRules.Mode && _vitalsRules.MaxPips > before.MaxPips)
            NotePace("pips", _vitalsRules.MaxPips);
    }
}
