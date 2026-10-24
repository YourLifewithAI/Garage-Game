using System;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The three meters (docs/game/MECHANICS.md sections 1, 4 and 5; the mechanics round's packet M2): hearts in quarters, the bar under
/// them and the pips as a row under the bar, always visible in the panel at the upper left (which holds nothing else since decision
/// 66: the title, the state lines and the buttons are in the H help). The pips are that row always, and nothing round the Gubble
/// (decision 51, which replaced decision 38's row that showed only while the Gubble was out of frame). They are drawn, not written: no
/// numbers unless the player's own setting "Show numbers on the meters" is on (it lives in the machine's profile, off by default,
/// as the other switches do). Everything is read from the host (Vitals, BodyRules, Mode), never kept here; the HUD asks no
/// command for it and changes nothing.
/// - In Creative the hearts and the bar are drawn full and grey, and the pool is a shadow: soft translucent pips that drain and
///   refill but never refuse, with a faint "needs N" where an ability costs more than the shadow holds.
/// - Elsewhere the price of an ability is what the player pays (the ordered price, by principal): dots on the wheel and the key
///   hints, and "needs N" while the pool is short of it.
/// Cost: a frame compares a few numbers and redraws a meter only when one changed (the pips' row among them: nothing follows the
/// Gubble any more, so no meter redraws on its own).
/// </summary>
public partial class RoomHud
{
    /// <summary>The profile's key for the numbers option, and its value when the player never chose (the design: off).</summary>
    public const string NumbersProfileKey = "meter_numbers";
    public const bool DefaultMeterNumbers = false;

    public HeartsMeter Hearts => _hearts;
    public StaminaBar Bar => _bar;
    /// <summary>The pips as a row under the hearts and the bar, always (decision 51).</summary>
    public PipsMarker PipsRow => _pipsMarker;
    public Label HeartsNumbers => _heartsNumbers;
    public Label BarNumbers => _barNumbers;
    public CheckButton? NumbersToggle => _numbersToggle;
    /// <summary>Whether the meters carry numbers (the player's setting).</summary>
    public bool MeterNumbers { get; private set; }

    private VBoxContainer _meterBox = null!;
    private HeartsMeter _hearts = null!;
    private StaminaBar _bar = null!;
    private PipsMarker _pipsMarker = null!;
    private Label _heartsNumbers = null!;
    private Label _barNumbers = null!;
    private CheckButton? _numbersToggle;
    private Label _slotPrice = null!;
    private (int, int, int, float, float, float, bool, bool, int, int, bool, bool, object?)? _meterAsOf;
    private string?[]? _slotCaps;
    private object? _slotCapsFor;

    /// <summary>The meters' small pictures go with the HUD (the next HUD draws them again), so none outlives the engine's own resources.</summary>
    public override void _ExitTree()
    {
        MeterTextures.Release();
        // An Esc menu that paused the game lets it go (RoomHud.Pause.cs).
        ReleasePause();
    }

    /// <summary>"Show numbers on the meters" as the profile says: off unless the player turned it on.</summary>
    public static bool LoadMeterNumbers(string profilePath)
    {
        var config = new ConfigFile();
        if (config.Load(profilePath) != Error.Ok) return DefaultMeterNumbers;
        var value = config.GetValue("profile", NumbersProfileKey, DefaultMeterNumbers);
        return value.VariantType == Variant.Type.Bool && value.AsBool();
    }

    /// <summary>The hearts, the bar and the pips' row in the upper-left panel.</summary>
    private void BuildMeters(Node column)
    {
        MeterNumbers = LoadMeterNumbers(ProfilePath);
        _meterBox = new VBoxContainer { Name = "Meters", Visible = false };
        _meterBox.AddThemeConstantOverride("separation", 4);
        column.AddChild(_meterBox);
        var heartsRow = new HBoxContainer { Name = "HeartsRow" };
        _meterBox.AddChild(heartsRow);
        _hearts = new HeartsMeter { Name = "Hearts", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        heartsRow.AddChild(_hearts);
        _heartsNumbers = new Label { Name = "HeartsNumbers", Visible = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        heartsRow.AddChild(_heartsNumbers);
        var barRow = new HBoxContainer { Name = "BarRow" };
        _meterBox.AddChild(barRow);
        _bar = new StaminaBar { Name = "StaminaBar", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        barRow.AddChild(_bar);
        _barNumbers = new Label { Name = "BarNumbers", Visible = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        barRow.AddChild(_barNumbers);
        // Under the bar, the pips' row: hearts, then stamina, then pips, always (decision 51).
        _pipsMarker = new PipsMarker { Name = "PipsMarker", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        _meterBox.AddChild(_pipsMarker);
    }

    /// <summary>The price beside the Gubble's first key in the H help's buttons: dots, or "needs N" (faint in Creative).</summary>
    private void BuildSlotPrice(Node actions)
    {
        _slotPrice = new Label { Name = "SlotPrice", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        actions.AddChild(_slotPrice);
        _keyTexts.Add((words =>
        {
            _slotPrice.Text = words;
            _slotPrice.Visible = words.Length > 0;
            var shadow = Host?.BodyRules.ShadowPool == true;
            _slotPrice.Modulate = shadow && words.StartsWith("needs", StringComparison.Ordinal) ? new Color(1, 1, 1, 0.5f) : Colors.White;
        }, () => SlotTag(0).Text));
    }

    /// <summary>The player's switch "Show numbers on the meters", with the other switches (profile, off by default).</summary>
    private void BuildNumbersSetting(Node options)
    {
        _numbersToggle = new CheckButton
        {
            Name = "MeterNumbersSetting", Text = "Show numbers on the meters", ButtonPressed = MeterNumbers,
            TooltipText = NumbersTooltip(),
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
        };
        _numbersToggle.Toggled += SetMeterNumbers;
        options.AddChild(_numbersToggle);
    }

    /// <summary>
    /// The switch's hint, in the island's words for the three meters (the pack's names block: MeterWords); today's words when the pack
    /// has none. Made again when the meters are (the rules they are drawn under are part of their state).
    /// </summary>
    private string NumbersTooltip() => Host?.Rules?.Names == null
        ? "Hearts, the stamina bar and the pips are drawn, not written. This adds their numbers beside them."
        : $"{MeterWords.Name(Host, "hearts")}, the {MeterWords.Lower(Host, "stamina")} and the {MeterWords.Lower(Host, "pips")} are drawn, not written. This adds their numbers beside them.";

    /// <summary>The player's choice, kept in the profile (this session only if it cannot be written).</summary>
    public void SetMeterNumbers(bool shown)
    {
        MeterNumbers = shown;
        if (_numbersToggle != null && _numbersToggle.ButtonPressed != shown) _numbersToggle.SetPressedNoSignal(shown);
        var saved = SaveProfileValue(NumbersProfileKey, shown);
        _meterAsOf = null;
        _noticeText = (shown ? "The meters show their numbers." : "The meters are drawn without numbers.") + (saved ? "" : " (Not saved: it lasts this session.)");
    }

    // ---- what an ability costs the player ----

    /// <summary>The capability in each of the five ability slots (null: the island has none), made again only when the island's rules change.</summary>
    private string?[] SlotCaps()
    {
        var rules = Host?.Rules;
        if (_slotCaps == null || !ReferenceEquals(_slotCapsFor, rules))
        {
            _slotCapsFor = rules;
            _slotCaps = new string?[Act.GubbleSlots.Length];
            for (var i = 0; i < _slotCaps.Length; i++) _slotCaps[i] = SlotAbility(i)?.Capability;
        }
        return _slotCaps;
    }

    /// <summary>The pips the player has now (0 with no pool).</summary>
    private int PipBalance() => Host is { } host && host.BodyRules.HasPool ? Math.Max(0, host.Vitals.Pips) : 0;

    /// <summary>What a slot's ability costs the player, in whole pips: the ordered price, by principal, never a field. 0 without a pool, a price or the ability.</summary>
    public int SlotPrice(int slot)
    {
        if (Host is not { } host || !host.BodyRules.HasPool || slot < 0 || slot >= Act.GubbleSlots.Length || SlotCaps()[slot] is not { } capability) return 0;
        return host.PriceFor(capability, CommandHost.PlayerPrincipal);
    }

    /// <summary>Whether the pool is short of a slot's price now ("needs N"). In Creative's shadow pool the cast still happens.</summary>
    public bool SlotNeeds(int slot) => SlotPrice(slot) is var price and > 0 && PipBalance() < price;

    /// <summary>What a slot's ability costs the player as it is drawn now: its dots, or "needs N"; nothing without a pool or a price. Builds a string: not for every frame.</summary>
    public MeterMath.PriceTag SlotTag(int slot) => MeterMath.Tag(SlotPrice(slot), PipBalance());

    /// <summary>A slot's price for the wheel: the words, whether the pool is short of it, and whether this is Creative's shadow pool (faint).</summary>
    private GubbleWheel.Price? WheelPrice(int slot)
    {
        // A locked wedge (Fire before the first rest) shows its reason, not a price it cannot be cast for.
        if (SlotLockedWords(slot) != null) return null;
        var tag = SlotTag(slot);
        return tag.Shown ? new GubbleWheel.Price(tag.Text, tag.Needs, Host?.BodyRules.ShadowPool == true) : null;
    }

    /// <summary>
    /// What the key words depend on in the pool, as a number: which slots the pool cannot pay and whether it is the shadow pool.
    /// The key texts are made again when it changes (RefreshKeyTextIfChanged) and not otherwise.
    /// </summary>
    private int PoolHint()
    {
        if (Host is not { } host || !host.BodyRules.HasPool) return 0;
        var hint = 1 | (host.BodyRules.ShadowPool ? 2 : 0);
        for (var i = 0; i < Act.GubbleSlots.Length; i++)
            if (SlotNeeds(i)) hint |= 4 << i;
        return hint;
    }

    /// <summary>" ●●" after a slot's name in the help, or " needs 4"; nothing when the ability has no price.</summary>
    private string SlotPriceSuffix(int slot) => SlotTag(slot) is { Shown: true } tag ? " " + tag.Text : "";

    // ---- every frame ----

    /// <summary>The meters from the host's numbers, and the pips at the Gubble. Drawn only when something changed.</summary>
    private void UpdateMeters(double delta)
    {
        var host = Host;
        if (host == null || !IsInstanceValid(host) || IntroPlaying)
        {
            if (_meterBox.Visible) _meterBox.Visible = _meterPanel.Visible = false;
            _pipsMarker.Present(false, delta);
            TickJournalPulse(null, delta);
            return;
        }
        if (!_meterBox.Visible) _meterBox.Visible = _meterPanel.Visible = true;
        var rules = host.BodyRules;
        var vitals = host.Vitals;
        var creative = host.Mode == "creative";
        var maxQuarters = rules.MaxQuarters(vitals);
        // Creative draws everything full and grey: nothing hurts or drains there.
        var quarters = creative ? maxQuarters : rules.Quarters(vitals);
        var maxM = vitals.MaxStamina;
        var staminaM = creative ? maxM : vitals.Stamina;
        var rowM = MeterMath.RowCapacityM(rules);
        var bandM = MeterMath.BandM(rules);
        var slowed = !creative && host.StaminaSlowed;
        var hasPool = rules.HasPool;
        var pips = hasPool ? Math.Max(0, vitals.Pips) : 0;
        var maxPips = hasPool ? rules.MaxPips : 0;
        var shadow = hasPool && rules.ShadowPool;
        var state = (quarters, maxQuarters, (int)Math.Round(staminaM * 500), maxM, rowM, bandM, creative, slowed, pips, maxPips, shadow, MeterNumbers, (object?)host.Rules);
        if (_meterAsOf != state)
        {
            _meterAsOf = state;
            _hearts.Set(quarters, maxQuarters, creative);
            _bar.Set(staminaM, maxM, rowM, bandM, creative, slowed);
            var aura = Companion.AuraColor ?? Companion.AppearanceColor;
            var pipsNumbers = MeterNumbers && hasPool ? MeterMath.PipsNumbers(MeterWords.Name(host, "pips"), pips, maxPips) : "";
            _pipsMarker.SetPool(pips, maxPips, shadow, aura);
            _pipsMarker.SetNumbers(pipsNumbers);
            _heartsNumbers.Visible = _barNumbers.Visible = MeterNumbers;
            if (_numbersToggle != null) _numbersToggle.TooltipText = NumbersTooltip();
            if (MeterNumbers)
            {
                _heartsNumbers.Text = MeterMath.HeartsNumbers(MeterWords.Name(host, "hearts"), quarters, maxQuarters);
                _barNumbers.Text = MeterMath.BarNumbers(MeterWords.Name(host, "stamina"), staminaM, maxM);
            }
        }
        _pipsMarker.Present(true, delta);
        TickSpendHint(host, delta);
        TickJournalPulse(host, delta);
    }

    /// <summary>An empty pool refused a cast: the pips in the row under the bar blink.</summary>
    private void FlashPipMeters() => _pipsMarker.Flash();
}
